using System;
using System.Threading.Tasks;
using Huateng.Turntable.Sdk;

namespace TurntableStudio.Services
{
    /// <summary>
    /// 封装转盘 SDK TurntableController，提供 Connect/Disconnect/MoveTo/ReadAngle
    /// </summary>
    public sealed class TurntableService : IDisposable
    {
        private TurntableController? _controller;
        private TurntableOptions? _options;
        private bool _disposed;

        public bool IsConnected { get; private set; }
        public event EventHandler<string>? StatusChanged;

        public void Connect(string plcIp, int port = 502, byte slaveId = 1)
        {
            _options = new TurntableOptions
            {
                PlcIp = plcIp,
                Port = port,
                SlaveId = slaveId,
                TimeoutMilliseconds = 1500,
                ReadRetryCount = 3,
                ReadRetryDelayMs = 100,
            };
            _controller = new TurntableController(_options);
            _controller.ConnectionChanged += OnConnectionChanged;
            _controller.Connect();
        }

        public void Disconnect()
        {
            if (_controller == null) return;
            _controller.ConnectionChanged -= OnConnectionChanged;
            _controller.Disconnect();
            _controller.Dispose();
            _controller = null;
            IsConnected = false;
        }

        /// <summary>
        /// 转动到目标角度，之后轮询直到实测角度与目标在容差内停稳，或超时。
        /// toleranceDeg：停稳容差（默认 0.5°）
        /// stabilizeMs：确认停稳所需的稳定时间窗口（默认 300ms）
        /// timeoutMs：总等待超时（默认 8000ms）
        /// </summary>
        public async Task MoveToAsync(double targetDeg, double toleranceDeg = 0.5,
            int stabilizeMs = 300, int timeoutMs = 8000)
        {
            EnsureConnected();
            _controller!.MoveTo(targetDeg);

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            DateTime? stableStart = null;

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
                double current = _controller.ReadAngleDegrees();
                double diff = Math.Abs(current - targetDeg);
                // 处理 0°/360° 跨边界情况
                if (diff > 180) diff = 360 - diff;

                if (diff <= toleranceDeg)
                {
                    stableStart ??= DateTime.UtcNow;
                    if ((DateTime.UtcNow - stableStart.Value).TotalMilliseconds >= stabilizeMs)
                        return; // 停稳确认
                }
                else
                {
                    stableStart = null; // 仍在运动，重置稳定计时
                }
            }
            throw new TimeoutException($"转盘 MoveTo({targetDeg:F1}°) 超时 {timeoutMs}ms，当前角度 {_controller.ReadAngleDegrees():F1}°");
        }

        public double ReadAngle()
        {
            EnsureConnected();
            return _controller!.ReadAngleDegrees();
        }

        private void EnsureConnected()
        {
            if (_controller == null || !IsConnected)
                throw new InvalidOperationException("转盘未连接，请先调用 Connect()");
        }

        private void OnConnectionChanged(object sender, ConnectionChangedEventArgs e)
        {
            IsConnected = e.State == ConnectionState.Connected;
            StatusChanged?.Invoke(this, $"PLC [{e.Target}] → {e.State}: {e.Message}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }
    }
}
