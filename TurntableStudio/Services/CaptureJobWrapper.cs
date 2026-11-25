using System;
using System.Threading;
using System.Threading.Tasks;
using Huateng.Turntable.Sdk;

namespace TurntableStudio.Services
{
    public sealed class CaptureProgressEventArgs : EventArgs
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string FileName { get; set; } = string.Empty;
    }

    public sealed class CaptureJobWrapper : IDisposable
    {
        private readonly TurntableController _controller;
        private CaptureJob? _job;
        private TaskCompletionSource<bool>? _runCompletion;
        private bool _disposed;

        public event EventHandler<CaptureProgressEventArgs>? Progress;
        public event EventHandler<string>? Log;

        public CaptureJobWrapper(TurntableController controller)
        {
            _controller = controller;
        }

        public void Initialize(string sdkRoot)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CaptureJobWrapper));
            if (_runCompletion != null) throw new InvalidOperationException("正在拍摄中");

            _job?.Dispose();
            _job = null;

            var connection = CameraConnection.ForSonyA7M5(sdkRoot, CameraInterface.Usb);
            var job = new CaptureJob(_controller, connection);
            job.Log += (_, message) => Log?.Invoke(this, message);
            job.Error += (_, message) => Log?.Invoke(this, $"[错误] {message}");
            job.Progress += (_, progress) => Progress?.Invoke(this, new CaptureProgressEventArgs
            {
                Current = progress.ShotIndex,
                Total = progress.ShotCount,
                FileName = progress.LastFile ?? string.Empty
            });
            _job = job;
        }

        public Task ConnectCameraAsync()
        {
            var job = EnsureJob();
            return Task.Run(() =>
            {
                if (!job.TryConnectCamera(out string error))
                    throw new InvalidOperationException($"相机连接失败：{error}");
            });
        }

        public void DisconnectCamera() => _job?.DisconnectCamera();

        public Task StartLiveViewAsync()
        {
            EnsureJob().StartLiveView();
            return Task.CompletedTask;
        }

        public void StopLiveView() => _job?.StopLiveView();

        public void SubscribeLiveFrame(EventHandler<LiveFrameEventArgs> handler)
            => EnsureJob().LiveFrame += handler;

        public void UnsubscribeLiveFrame(EventHandler<LiveFrameEventArgs> handler)
        {
            if (_job != null) _job.LiveFrame -= handler;
        }

        public async Task<bool> RunAsync(int shotCount, string saveFolder, string filePrefix,
            ushort speed, ushort pulse, ushort accelDecel, CancellationToken ct)
        {
            var job = EnsureJob();
            if (_runCompletion != null) throw new InvalidOperationException("正在拍摄中");
            ct.ThrowIfCancellationRequested();

            var settings = CaptureJobSettings.ForFullCircle(shotCount);
            settings.SaveFolder = saveFolder;
            settings.FilePrefix = filePrefix;
            settings.Speed = speed;
            settings.Pulse = pulse;
            settings.AccelDecel = accelDecel;
            settings.ZeroBeforeStart = false;

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? error = null;
            void OnError(object? sender, string message) => error = message;
            void OnFinished(object? sender, CaptureJobResultEventArgs result)
            {
                if (result.Cancelled)
                    completion.TrySetCanceled();
                else if (!string.IsNullOrEmpty(error))
                    completion.TrySetException(new InvalidOperationException(error));
                else if (result.Saved != shotCount)
                    completion.TrySetException(new InvalidOperationException($"已保存 {result.Saved}/{shotCount} 张"));
                else
                    completion.TrySetResult(true);
            }

            bool wasConnected = _controller.Connected;
            _runCompletion = completion;
            job.Error += OnError;
            job.Finished += OnFinished;
            try
            {
                job.StartOneClick(settings);
                using var registration = ct.Register(job.Cancel);
                return await completion.Task;
            }
            finally
            {
                job.Finished -= OnFinished;
                job.Error -= OnError;
                _runCompletion = null;
                if (!_disposed && !wasConnected && _controller.Connected)
                    _controller.Disconnect();
            }
        }

        public void Cancel() => _job?.Cancel();

        private CaptureJob EnsureJob()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CaptureJobWrapper));
            return _job ?? throw new InvalidOperationException("请先调用 Initialize(sdkRoot)");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _job?.Dispose();
            }
            finally
            {
                _job = null;
                _runCompletion?.TrySetCanceled();
            }
        }
    }
}
