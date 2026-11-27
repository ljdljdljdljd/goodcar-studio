using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TurntableStudio.Services
{
    /// <summary>
    /// P/Invoke 封装 SonyCrBridge.dll — ABI 严格匹配华腾 SDK 内部 P/Invoke 声明：
    ///   Initialize(string sdkRoot)
    ///   GetDeviceDescriptor(int index, StringBuilder buffer, int bufferSize)
    ///   Connect(string descriptorJson, int interfaceType)
    ///   GetLiveViewJpeg(out IntPtr data, out int length)   — bridge-owned buffer, do NOT free
    ///   TakePicture(string targetPath, int timeoutMs)
    /// </summary>
    public sealed class CameraService : IDisposable
    {
        private const string BridgeDll = "SonyCrBridge.dll";
        private bool _disposed;
        private bool _initialized;
        private bool _captureRequiresReconnect;

        #region P/Invoke — exact ABI match to Huateng.Turntable.Sdk.Internal.SonyDevice

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int CrBridge_Initialize([MarshalAs(UnmanagedType.LPStr)] string? sdkRoot);

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int CrBridge_GetDeviceDescriptor(
            int index,
            [MarshalAs(UnmanagedType.LPStr)] StringBuilder? buffer,
            int bufferSize);

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int CrBridge_Connect(
            [MarshalAs(UnmanagedType.LPStr)] string descriptorJson,
            int interfaceType);

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int CrBridge_Disconnect();

        /// <summary>
        /// data points into bridge-owned memory — do NOT call CrBridge_FreeBuffer.
        /// Copy on the calling thread, before its next GetLiveViewJpeg call.
        /// </summary>
        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int CrBridge_GetLiveViewJpeg(out IntPtr data, out int length);

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int CrBridge_FreeBuffer(IntPtr data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ShouldCancel();

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int CrBridge_TakePictureEx(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string targetPath,
            int timeoutMs,
            ShouldCancel shouldCancel);

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int CrBridge_SetSaveInfo(
            [MarshalAs(UnmanagedType.LPStr)] string path,
            [MarshalAs(UnmanagedType.LPStr)] string prefix,
            int startNo);

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int CrBridge_LastError(
            [MarshalAs(UnmanagedType.LPStr)] StringBuilder? buffer,
            int capacity);

        #endregion

        public bool IsConnected { get; private set; }

        /// <summary>
        /// 初始化索尼 SDK。
        /// sdkRoot：Cr_Core.dll 所在目录路径；null 或空字符串表示使用程序当前目录。
        /// </summary>
        public void Initialize(string? sdkRoot = null)
        {
            int result = CrBridge_Initialize(sdkRoot);
            if (result < 0)
                throw new InvalidOperationException(
                    $"CrBridge_Initialize 失败：{GetLastError()} (code {result})");
            _initialized = true;
        }

        /// <summary>
        /// 枚举已连接相机，返回相机数量。
        /// 注意：GetDeviceDescriptor(0..count-1) 才能拿到描述符；不直接暴露 GetDeviceCount。
        /// </summary>
        public int GetDeviceCount()
        {
            // 两步：先 GetDeviceDescriptor(0, null, 0) 探测是否有设备
            int probe = CrBridge_GetDeviceDescriptor(0, null, 0);
            if (probe == (int)CrBridgeInternalError.InvalidIndex)
                return 0;
            if (probe < 0)
                throw new InvalidOperationException(
                    $"探测相机失败：{GetLastError()} (code {probe})");
            return 1; // at least one; caller should enumerate until InvalidIndex
        }

        /// <summary>获取指定索引相机的 JSON 描述（含 idHex / model / connectionType 等）</summary>
        public string GetDeviceDescriptor(int index)
        {
            int required = CrBridge_GetDeviceDescriptor(index, null, 0);
            if (required == (int)CrBridgeInternalError.InvalidIndex)
                throw new ArgumentOutOfRangeException(nameof(index), "相机索引超出范围");
            if (required < 0)
                throw new InvalidOperationException(
                    $"CrBridge_GetDeviceDescriptor 失败：{GetLastError()} (code {required})");
            var sb = new StringBuilder(required + 1);
            CrBridge_GetDeviceDescriptor(index, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>
        /// 连接相机。
        /// descriptorJson：由 GetDeviceDescriptor 获取的 JSON 字符串。
        /// interfaceType：0=USB（默认），1=WiredLan，2=WiFi。
        /// </summary>
        public void Connect(string descriptorJson, int interfaceType = 0)
        {
            int result = CrBridge_Connect(descriptorJson, interfaceType);
            if (result < 0)
                throw new InvalidOperationException(
                    $"CrBridge_Connect 失败：{GetLastError()} (code {result})");
            IsConnected = true;
            _captureRequiresReconnect = false;
        }

        /// <summary>断开相机连接并释放 SDK 资源</summary>
        public void Disconnect()
        {
            if (!_initialized) return;
            int result = CrBridge_Disconnect();
            IsConnected = false;
            if (result < 0)
                throw new InvalidOperationException(
                    $"CrBridge_Disconnect 失败：{GetLastError()} (code {result})");
            _initialized = false;
        }

        /// <summary>
        /// 获取当前一帧 LiveView JPEG 字节数组。
        /// Bridge 返回堆分配拷贝，调用后必须释放。
        /// </summary>
        public byte[]? GetLiveViewJpeg()
        {
            IntPtr data = IntPtr.Zero;
            int length = 0;
            int result = CrBridge_GetLiveViewJpeg(out data, out length);
            if (result == -3)
                throw new InvalidOperationException("相机已断开，请重新连接。");
            if (result < 0 || data == IntPtr.Zero || length <= 0) return null;
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
                return bytes;
            }
            finally
            {
                CrBridge_FreeBuffer(data);
            }
        }

        /// <summary>
        /// 触发快门拍照，等待图片下载后移动到 targetPath。
        /// timeoutMs=0 → bridge 默认 30 秒超时。
        /// </summary>
        public void TakePicture(string targetPath, int timeoutMs = 0, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnsureCaptureReady();
            int result = CrBridge_TakePictureEx(targetPath, timeoutMs, () => ct.IsCancellationRequested ? 1 : 0);
            if (result == -12) throw new IOException($"下载文件校验失败：{GetLastError()}");
            if (result < 0) _captureRequiresReconnect = true;
            if (result == -11) throw new OperationCanceledException(ct);
            if (result == -5) throw new TimeoutException(GetLastError());
            if (result < 0)
                throw new InvalidOperationException(
                    $"CrBridge_TakePictureEx 失败：{GetLastError()} (code {result})");
        }

        public void EnsureCaptureReady()
        {
            if (!IsConnected || _captureRequiresReconnect)
                throw new InvalidOperationException("相机未连接或上一张快门结果不确定，请重新连接相机后再拍摄。");
        }

        /// <summary>读取桥接层最近一次错误描述</summary>
        public string GetLastError()
        {
            int required = CrBridge_LastError(null, 0);
            if (required <= 1) return string.Empty;
            var sb = new StringBuilder(required + 1);
            CrBridge_LastError(sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>
        /// 设置 PC 端图片保存路径，必须在 Connect 之后、TakePicture 之前调用。
        /// Sony SDK 默认只存相机卡，不设置则 OnCompleteDownload 永远不来。
        /// </summary>
        public void SetSaveInfo(string saveFolder, string prefix = "Shot")
        {
            int result = CrBridge_SetSaveInfo(saveFolder, prefix, 1);
            if (result < 0)
                throw new InvalidOperationException(
                    $"CrBridge_SetSaveInfo 失败：{GetLastError()} (code {result})");
        }

        public void Dispose()
        {
            if (_disposed) return;
            Disconnect();
            _disposed = true;
        }

        private enum CrBridgeInternalError
        {
            InvalidIndex = -6
        }
    }
}
