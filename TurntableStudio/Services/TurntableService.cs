using System;
using Huateng.Turntable.Sdk;

namespace TurntableStudio.Services
{
    /// <summary>
    /// 封装转盘 SDK 的连接、角度回读和脉冲操作。
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

        public void SetDirection(bool forward)
        {
            EnsureConnected();
            _controller!.SetDirection(forward ? MovementDirection.Forward : MovementDirection.Reverse);
        }

        public void StartRotation()
        {
            EnsureConnected();
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
            _controller!.GoHome();
        }

        public void Configure(ushort speed, ushort pulse, ushort accelDecel)
        {
            EnsureConnected();
            _controller!.Configure(speed, pulse, accelDecel);
        }

        public void MoveToWithDirection(double targetDeg, bool forward)
        {
            EnsureConnected();
            _controller!.SetDirection(forward ? MovementDirection.Forward : MovementDirection.Reverse);
            _controller!.WriteTargetAngle((ushort)targetDeg);
            _controller!.Start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            Disconnect();
            _disposed = true;
        }
    }
}
