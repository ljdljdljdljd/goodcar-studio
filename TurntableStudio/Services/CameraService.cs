using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TurntableStudio.Services
{
    /// <summary>
    /// P/Invoke 封装 SonyCrBridge.dll 的全部 11 个 C 导出函数（cdecl, ANSI）
    /// </summary>
    public sealed class CameraService : IDisposable
    {
        private const string BridgeDll = "SonyCrBridge.dll";
        private bool _disposed;

        #region P/Invoke 原生声明（严格匹配 SonyCrBridge.h）

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int CrBridge_Initialize();

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int CrBridge_GetDeviceCount();

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int CrBridge_GetDeviceDescriptor(int index, StringBuilder? buffer, int capacity);

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int CrBridge_Connect(int index, uint timeoutMs);

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int CrBridge_Disconnect();

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int CrBridge_StartLiveView();

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int CrBridge_StopLiveView();

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int CrBridge_GetLiveViewJpeg(out IntPtr data, out uint size, uint timeoutMs);

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void CrBridge_FreeBuffer(IntPtr buffer);

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int CrBridge_TakePicture(uint holdMs);

        [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int CrBridge_LastError(StringBuilder? buffer, int capacity);

        #endregion

        public bool IsConnected { get; private set; }

        /// <summary>初始化索尼 SDK（调用 CrBridge_Initialize）</summary>
        public void Initialize()
        {
            int result = CrBridge_Initialize();
            if (result < 0)
                throw new InvalidOperationException($"CrBridge_Initialize 失败：{GetLastError()} (code {result})");
        }

        /// <summary>枚举已连接相机数量</summary>
        public int GetDeviceCount()
        {
            int count = CrBridge_GetDeviceCount();
            if (count < 0)
                throw new InvalidOperationException($"CrBridge_GetDeviceCount 失败：{GetLastError()} (code {count})");
            return count;
        }

        /// <summary>获取指定索引相机的 JSON 描述（含 model/idHex/IP 等）</summary>
        public string GetDeviceDescriptor(int index)
        {
            int required = CrBridge_GetDeviceDescriptor(index, null, 0);
            if (required < 0)
                throw new InvalidOperationException($"CrBridge_GetDeviceDescriptor 失败：{GetLastError()} (code {required})");
            var sb = new StringBuilder(required);
            CrBridge_GetDeviceDescriptor(index, sb, required);
            return sb.ToString();
        }

        /// <summary>连接指定索引的相机，timeoutMs=0 使用默认 15 秒</summary>
        public void Connect(int index, uint timeoutMs = 0)
        {
            int result = CrBridge_Connect(index, timeoutMs);
            if (result < 0)
                throw new InvalidOperationException($"CrBridge_Connect 失败：{GetLastError()} (code {result})");
            IsConnected = true;
        }

        /// <summary>断开相机连接并释放 SDK 资源</summary>
        public void Disconnect()
        {
            if (!IsConnected) return;
            CrBridge_Disconnect();
            IsConnected = false;
        }

        /// <summary>开启实时取景流</summary>
        public void StartLiveView()
        {
            int result = CrBridge_StartLiveView();
            if (result < 0)
                throw new InvalidOperationException($"CrBridge_StartLiveView 失败：{GetLastError()} (code {result})");
        }

        /// <summary>停止实时取景流</summary>
        public void StopLiveView()
        {
            CrBridge_StopLiveView();
        }

        /// <summary>
        /// 获取当前一帧 LiveView JPEG 字节数组。
        /// 内部调用 CrBridge_GetLiveViewJpeg + CrBridge_FreeBuffer，外部直接使用 byte[]。
        /// </summary>
        public byte[]? GetLiveViewJpeg(uint timeoutMs = 1000)
        {
            int result = CrBridge_GetLiveViewJpeg(out IntPtr data, out uint size, timeoutMs);
            if (result < 0 || data == IntPtr.Zero || size == 0) return null;
            try
            {
                byte[] bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, (int)size);
                return bytes;
            }
            finally
            {
                CrBridge_FreeBuffer(data);
            }
        }

        /// <summary>
        /// 触发快门拍照（Shutter Down 保持 holdMs 后 Shutter Up）。
        /// 注意：原图异步传输，需等待 OnCompleteDownload 回调（由 CaptureOrchestrator 负责轮询文件落盘）。
        /// </summary>
        public void TakePicture(uint holdMs = 0)
        {
            int result = CrBridge_TakePicture(holdMs);
            if (result < 0)
                throw new InvalidOperationException($"CrBridge_TakePicture 失败：{GetLastError()} (code {result})");
        }

        /// <summary>读取桥接层最近一次错误描述</summary>
        public string GetLastError()
        {
            int required = CrBridge_LastError(null, 0);
            if (required <= 1) return string.Empty;
            var sb = new StringBuilder(required);
            CrBridge_LastError(sb, required);
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Disconnect(); } catch { }
        }
    }
}
