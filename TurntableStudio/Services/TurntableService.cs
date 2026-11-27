using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Huateng.Turntable.Sdk;

namespace TurntableStudio.Services
{
    /// <summary>
    /// 封装转盘 SDK 的连接、角度回读和脉冲操作。
    /// </summary>
    public sealed class TurntableService : IDisposable
    {
        private const double IdleThresholdDeg = 0.3;
        private const long IdleMinIntervalMs = 80;

        private TurntableController? _controller;
        private TurntableOptions? _options;
        private bool _disposed;
        private double _prevAngleForIdle = double.NaN;
        private long _prevAngleTickForIdle;

        public bool IsConnected { get; private set; }
        public event EventHandler<string>? StatusChanged;

        public void Connect(string plcIp, int port = 502, byte slaveId = 1)
        {
            _prevAngleForIdle = double.NaN;
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
            _prevAngleForIdle = double.NaN;
        }

        public double ReadAngle()
        {
            EnsureConnected();
            return _controller!.ReadAngleDegrees();
        }

        /// <summary>
        /// 通过间隔至少 80ms 的两次角度读数估算停稳，首次采样返回 false。
        /// </summary>
        public bool IsRotationIdle()
        {
            var controller = _controller;
            if (controller == null || !IsConnected) return false;

            double angle = controller.ReadAngleDegrees();
            long now = Stopwatch.GetTimestamp();
            long elapsedMs = (now - _prevAngleTickForIdle) * 1000 / Stopwatch.Frequency;
            bool idle = IsConnected && elapsedMs >= IdleMinIntervalMs &&
                        Math.Abs(angle - _prevAngleForIdle) < IdleThresholdDeg;

            _prevAngleForIdle = IsConnected && !double.IsInfinity(angle) ? angle : double.NaN;
            _prevAngleTickForIdle = now;
            return idle;
        }

        private void EnsureConnected()
        {
            if (_controller == null || !IsConnected)
                throw new InvalidOperationException("转盘未连接，请先调用 Connect()");
        }

        private void OnConnectionChanged(object sender, ConnectionChangedEventArgs e)
        {
            IsConnected = e.State == ConnectionState.Connected;
            if (!IsConnected) _prevAngleForIdle = double.NaN;
            StatusChanged?.Invoke(this, $"PLC [{e.Target}] → {e.State}: {e.Message}");
        }

        public void SetDirection(bool forward)
        {
            EnsureConnected();
            _controller!.SetDirection(forward ? MovementDirection.Forward : MovementDirection.Reverse);
        }

        public void StartRotation()
        {
            EnsureConnected();
            _prevAngleForIdle = double.NaN;
            _controller!.Start();
        }

        public void StopRotation()
        {
            var controller = _controller ?? throw new InvalidOperationException("PLC 控制器未建立，无法发送停止指令。");
            controller.Stop();
        }

        public void GoHome()
        {
            EnsureConnected();
            _prevAngleForIdle = double.NaN;
            _controller!.GoHome();
        }

        public void Configure(ushort speed, ushort pulse, ushort accelDecel)
        {
            EnsureConnected();
            _controller!.Configure(speed, pulse, accelDecel);
        }

        /// <summary>
        /// 按指定方向转动，直到读数进入目标角度容差，然后发送停止指令。
        /// 超时或取消时也发送停止指令。寄存器 1518 保持 360°。
        /// </summary>
        public async Task MoveToAsync(double targetDeg, bool forward,
            double toleranceDeg = 1.0, int timeoutMs = 30000, CancellationToken ct = default)
        {
            if (!(targetDeg >= 0 && targetDeg < 360))
                throw new ArgumentOutOfRangeException(nameof(targetDeg));
            if (!(toleranceDeg >= 0 && toleranceDeg <= 180))
                throw new ArgumentOutOfRangeException(nameof(toleranceDeg));
            if (timeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMs));

            ct.ThrowIfCancellationRequested();
            SetDirection(forward);
            ct.ThrowIfCancellationRequested();

            var clock = Stopwatch.StartNew();
            try
            {
                StartRotation();
                while (clock.ElapsedMilliseconds < timeoutMs)
                {
                    ct.ThrowIfCancellationRequested();
                    double current = ReadAngle();
                    ct.ThrowIfCancellationRequested();
                    if (clock.ElapsedMilliseconds >= timeoutMs) break;
                    if (!(current >= 0 && current < 360))
                        throw new InvalidOperationException($"无效的 PLC 角度读数：{current}。");

                    double delta = Math.Abs(current - targetDeg);
                    if (Math.Min(delta, 360 - delta) <= toleranceDeg) return;

                    await Task.Delay(80, ct).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"转动到 {targetDeg:F1}° 超时（{timeoutMs}ms）。");
            }
            finally
            {
                try { StopRotation(); }
                catch (Exception ex) { throw new InvalidOperationException("发送转盘停止指令失败。", ex); }
                finally { _prevAngleForIdle = double.NaN; }
            }
        }

        public void MoveToWithDirection(double targetDeg, bool forward)
        {
            SetDirection(forward);
            StartRotation();
        }

        public void Dispose()
        {
            if (_disposed) return;
            Disconnect();
            _disposed = true;
        }
    }
}
