using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TurntableStudio.Services;

namespace TurntableStudio.ViewModels
{
    public sealed class LogEntry
    {
        public string Time { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public LogLevel Level { get; set; }
        public Brush Color => Level switch
        {
            LogLevel.Success => new SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 227, 161)),  // #A6E3A1
            LogLevel.Error   => new SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 139, 168)),  // #F38BA8
            LogLevel.Warning => new SolidColorBrush(System.Windows.Media.Color.FromRgb(249, 226, 175)),  // #F9E2AF
            _                => new SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 173, 200)),  // #A6ADC8
        };
    }

    public enum LogLevel { Info, Success, Error, Warning }

    public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly TurntableService _turntable = new();
        private readonly CameraService _camera = new();
        private CaptureOrchestrator? _orchestrator;
        private CancellationTokenSource? _captureCts;
        private System.Timers.Timer? _liveViewTimer;
        private System.Timers.Timer? _angleTimer;
        private bool _disposed;

        // ── 连接状态 ─────────────────────────────────────────────────
        private bool _plcConnected;
        public bool PlcConnected
        {
            get => _plcConnected;
            private set
            {
                Set(ref _plcConnected, value);
                OnPropertyChanged(nameof(PlcStatusText));
                OnPropertyChanged(nameof(PlcStatusColor));
                OnPropertyChanged(nameof(ConnectPlcLabel));
                OnPropertyChanged(nameof(Step1Done));
                OnPropertyChanged(nameof(CanStartCapture));
                RefreshStepStates();
            }
        }

        private bool _cameraConnected;
        public bool CameraConnected
        {
            get => _cameraConnected;
            private set
            {
                Set(ref _cameraConnected, value);
                OnPropertyChanged(nameof(CameraStatusText));
                OnPropertyChanged(nameof(CameraStatusColor));
                OnPropertyChanged(nameof(ConnectCameraLabel));
                OnPropertyChanged(nameof(Step2Done));
                OnPropertyChanged(nameof(CanStartCapture));
                RefreshStepStates();
            }
        }

        private bool _isCapturing;
        public bool IsCapturing
        {
            get => _isCapturing;
            private set
            {
                Set(ref _isCapturing, value);
                OnPropertyChanged(nameof(StartStopLabel));
                OnPropertyChanged(nameof(StartStopColor));
                OnPropertyChanged(nameof(CanStartCapture));
                OnPropertyChanged(nameof(CanChangeShotCount));
            }
        }

        private bool _liveViewActive;
        public bool LiveViewActive
        {
            get => _liveViewActive;
            private set { Set(ref _liveViewActive, value); OnPropertyChanged(nameof(LiveViewLabel)); }
        }

        // ── 状态文本 ─────────────────────────────────────────────────
        public string PlcStatusText => PlcConnected ? "已连接" : "未连接";
        public Brush PlcStatusColor => PlcConnected
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 227, 161))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 139, 168));

        public string CameraStatusText => CameraConnected ? "已连接" : "未连接";
        public Brush CameraStatusColor => CameraConnected
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 227, 161))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 139, 168));

        public string ConnectPlcLabel    => PlcConnected ? "断开 PLC" : "连接 PLC";
        public string ConnectCameraLabel => CameraConnected ? "断开相机" : "连接相机";
        public string LiveViewLabel      => LiveViewActive ? "关闭取景" : "开启取景";
        public string StartStopLabel     => IsCapturing ? "停止拍摄" : "开始拍摄";
        public Brush  StartStopColor     => IsCapturing
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 139, 168))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 227, 161));

        // ── 配置 ─────────────────────────────────────────────────────
        private string _plcIp = "192.168.1.111";
        public string PlcIp { get => _plcIp; set => Set(ref _plcIp, value); }

        private string _saveFolder = @"D:\GoodCar\shots";
        public string SaveFolder { get => _saveFolder; set => Set(ref _saveFolder, value); }

        private int _selectedShotCount = 72;
        public int SelectedShotCount
        {
            get => _selectedShotCount;
            set
            {
                if (!IsCapturing) Set(ref _selectedShotCount, value);
                OnPropertyChanged(nameof(StepAngle));
            }
        }
        public bool CanChangeShotCount => !IsCapturing;
        public ObservableCollection<int> ShotCountOptions { get; } = new() { 72, 36 };
        public string StepAngle => $"{360.0 / _selectedShotCount:F1}° / 张";

        // ── 角度 ─────────────────────────────────────────────────────
        private double _currentAngle;
        public double CurrentAngle
        {
            get => _currentAngle;
            private set
            {
                Set(ref _currentAngle, value);
                OnPropertyChanged(nameof(CurrentAngleText));
                OnPropertyChanged(nameof(DialRotation));
            }
        }
        public string CurrentAngleText => $"{_currentAngle:F1}°";
        public double DialRotation => _currentAngle; // 供 XAML RotateTransform 使用

        // ── 进度 ─────────────────────────────────────────────────────
        private int _progressCurrent;
        public int ProgressCurrent
        {
            get => _progressCurrent;
            private set { Set(ref _progressCurrent, value); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(ProgressPercent)); }
        }

        private int _progressTotal = 72;
        public int ProgressTotal
        {
            get => _progressTotal;
            private set { Set(ref _progressTotal, value); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(ProgressPercent)); }
        }

        public string ProgressText => _progressTotal > 0
            ? $"{_progressCurrent} / {_progressTotal}"
            : "—";
        public double ProgressPercent => _progressTotal > 0
            ? (double)_progressCurrent / _progressTotal * 100
            : 0;

        // ── 分步引导 ─────────────────────────────────────────────────
        public bool Step1Done => PlcConnected;
        public bool Step2Done => CameraConnected;
        public bool Step3Done => LiveViewActive;
        public bool Step4Active => PlcConnected && CameraConnected && !IsCapturing;

        private Brush StepDoneBrush    => new SolidColorBrush(System.Windows.Media.Color.FromRgb(166, 227, 161));
        private Brush StepActiveBrush  => new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 166, 247));
        private Brush StepPendingBrush => new SolidColorBrush(System.Windows.Media.Color.FromRgb(69, 71, 90));

        public Brush Step1Brush => Step1Done ? StepDoneBrush : StepActiveBrush;
        public Brush Step2Brush => Step1Done ? (Step2Done ? StepDoneBrush : StepActiveBrush) : StepPendingBrush;
        public Brush Step3Brush => Step2Done ? (Step3Done ? StepDoneBrush : StepActiveBrush) : StepPendingBrush;
        public Brush Step4Brush => Step4Active ? StepActiveBrush : (IsCapturing ? StepDoneBrush : StepPendingBrush);

        private void RefreshStepStates()
        {
            OnPropertyChanged(nameof(Step1Done));
            OnPropertyChanged(nameof(Step2Done));
            OnPropertyChanged(nameof(Step3Done));
            OnPropertyChanged(nameof(Step4Active));
            OnPropertyChanged(nameof(Step1Brush));
            OnPropertyChanged(nameof(Step2Brush));
            OnPropertyChanged(nameof(Step3Brush));
            OnPropertyChanged(nameof(Step4Brush));
        }

        // ── 实时取景 ─────────────────────────────────────────────────
        private BitmapImage? _liveViewImage;
        public BitmapImage? LiveViewImage { get => _liveViewImage; private set => Set(ref _liveViewImage, value); }

        // ── 日志流 ───────────────────────────────────────────────────
        public ObservableCollection<LogEntry> LogEntries { get; } = new();

        // ── 转盘手动控制属性 ────────────────────────────────────────────
        private ushort _turntableSpeed = 230;
        public ushort TurntableSpeed { get => _turntableSpeed; set => Set(ref _turntableSpeed, value); }

        private ushort _turntablePulse = 24690;
        public ushort TurntablePulse { get => _turntablePulse; set => Set(ref _turntablePulse, value); }

        private ushort _turntableAccelDecel = 1000;
        public ushort TurntableAccelDecel { get => _turntableAccelDecel; set => Set(ref _turntableAccelDecel, value); }

        private double _targetAngle = 360.0;
        public double TargetAngle { get => _targetAngle; set => Set(ref _targetAngle, value); }

        private bool _isForwardDirection = true;
        public bool IsForwardDirection { get => _isForwardDirection; set => Set(ref _isForwardDirection, value); }

        // ── 守卫 ─────────────────────────────────────────────────────
        public bool CanStartCapture => PlcConnected && CameraConnected && !IsCapturing;

        // ── 命令 ─────────────────────────────────────────────────────
        public ICommand ConnectPlcCommand    { get; }
        public ICommand ConnectCameraCommand { get; }
        public ICommand StartStopCommand     { get; }
        public ICommand ToggleLiveViewCommand{ get; }
        public ICommand BrowseFolderCommand  { get; }
        public ICommand WriteParamsCommand   { get; }
        public ICommand MoveForwardCommand   { get; }
        public ICommand MoveReverseCommand   { get; }
        public ICommand StartRotationCommand { get; }
        public ICommand StopRotationCommand  { get; }
        public ICommand GoHomeCommand        { get; }
        public ICommand ManualForwardCommand { get; }
        public ICommand ManualReverseCommand { get; }

        public MainViewModel()
        {
            _turntable.StatusChanged += (_, msg) => AddLog(msg, LogLevel.Info);

            ConnectPlcCommand     = new AsyncRelayCommand(ConnectPlcAsync);
            ConnectCameraCommand  = new AsyncRelayCommand(ConnectCameraAsync);
            StartStopCommand      = new AsyncRelayCommand(StartStopAsync);
            ToggleLiveViewCommand = new AsyncRelayCommand(ToggleLiveViewAsync);
            BrowseFolderCommand   = new RelayCommand(BrowseFolder);
            WriteParamsCommand    = new AsyncRelayCommand(WriteParamsAsync);
            MoveForwardCommand    = new AsyncRelayCommand(MoveForwardAsync);
            MoveReverseCommand    = new AsyncRelayCommand(MoveReverseAsync);
            StartRotationCommand  = new AsyncRelayCommand(StartRotationAsync);
            StopRotationCommand   = new RelayCommand(_ => SafeStop());
            GoHomeCommand         = new AsyncRelayCommand(GoHomeAsync);
            ManualForwardCommand  = new RelayCommand(_ => ManualJog(true));
            ManualReverseCommand  = new RelayCommand(_ => ManualJog(false));
        }

        // ── 连接 PLC ─────────────────────────────────────────────────
        private async Task ConnectPlcAsync()
        {
            if (PlcConnected)
            {
                StopAnglePolling();
                _turntable.Disconnect();
                PlcConnected = false;
                AddLog("PLC 已断开", LogLevel.Warning);
                return;
            }
            AddLog($"正在连接 PLC {PlcIp}…", LogLevel.Info);
            try
            {
                await Task.Run(() => _turntable.Connect(PlcIp));
                PlcConnected = true;
                AddLog("PLC 连接成功", LogLevel.Success);
                StartAnglePolling();
            }
            catch (Exception ex) { AddLog($"PLC 连接失败：{ex.Message}", LogLevel.Error); }
        }

        // ── 连接相机 ─────────────────────────────────────────────────
        private async Task ConnectCameraAsync()
        {
            if (CameraConnected)
            {
                _camera.Disconnect();
                CameraConnected = false;
                AddLog("相机已断开", LogLevel.Warning);
                return;
            }
            AddLog("初始化相机 SDK…", LogLevel.Info);
            try
            {
                string desc = string.Empty;
                int count = 0;
                await Task.Run(() =>
                {
                    // 新 ABI：Initialize(sdkRoot) → GetDeviceDescriptor(index) → Connect(descriptor, interfaceType)
                    // sdkRoot 为 null 时 bridge 使用程序目录（Cr_Core.dll 与 SonyCrBridge.dll 同目录）
                    _camera.Initialize(null);
                    desc = _camera.GetDeviceDescriptor(0);
                    _camera.Connect(desc, interfaceType: 0); // 0 = USB
                    count = 1;
                });
                CameraConnected = true;
                AddLog($"相机连接成功（共 {count} 台）", LogLevel.Success);
                AddLog($"设备：{desc}", LogLevel.Info);
            }
            catch (Exception ex) { AddLog($"相机连接失败：{ex.Message}", LogLevel.Error); }
        }

        // ── 开始 / 停止拍摄 ──────────────────────────────────────────
        private async Task StartStopAsync()
        {
            if (IsCapturing)
            {
                _captureCts?.Cancel();
                AddLog("正在停止拍摄…", LogLevel.Warning);
                return;
            }

            if (!PlcConnected || !CameraConnected)
            {
                AddLog("请先连接 PLC 与相机", LogLevel.Warning); return;
            }
            if (string.IsNullOrWhiteSpace(SaveFolder))
            {
                AddLog("请先指定保存目录", LogLevel.Warning); return;
            }

            _captureCts   = new CancellationTokenSource();
            IsCapturing   = true;
            ProgressCurrent = 0;
            ProgressTotal   = SelectedShotCount;

            _orchestrator = new CaptureOrchestrator(_turntable, _camera);
            _orchestrator.Progress += (_, e) => UI(() =>
            {
                ProgressCurrent = e.Current;
                ProgressTotal   = e.Total;
            });
            _orchestrator.Log += (_, msg) =>
            {
                var level = msg.StartsWith("  ✓") ? LogLevel.Success
                           : msg.StartsWith("  ✗") || msg.StartsWith("✗") ? LogLevel.Error
                           : msg.StartsWith("  ⚠") || msg.StartsWith("⚠") ? LogLevel.Warning
                           : LogLevel.Info;
                AddLog(msg, level);
            };

            try
            {
                AddLog($"开始精拍 {SelectedShotCount} 张 → {SaveFolder}", LogLevel.Info);
                await _orchestrator.RunAsync(SelectedShotCount, SaveFolder, "Shot", _captureCts.Token);
                AddLog($"拍摄完成，共 {ProgressCurrent} 张", LogLevel.Success);
            }
            catch (OperationCanceledException) { AddLog("拍摄已取消", LogLevel.Warning); }
            catch (Exception ex)               { AddLog($"拍摄出错：{ex.Message}", LogLevel.Error); }
            finally
            {
                IsCapturing = false;
                _captureCts?.Dispose();
                _captureCts = null;
                RefreshStepStates();
            }
        }

        // ── 实时取景 ─────────────────────────────────────────────────
        private async Task ToggleLiveViewAsync()
        {
            if (!CameraConnected) { AddLog("请先连接相机", LogLevel.Warning); return; }

            if (LiveViewActive)
            {
                _liveViewTimer?.Stop();
                _liveViewTimer?.Dispose();
                _liveViewTimer = null;
                // 新 ABI 无 StopLiveView
                LiveViewActive  = false;
                LiveViewImage   = null;
                AddLog("实时取景已关闭", LogLevel.Info);
            }
            else
            {
                // 新 ABI 无 StartLiveView，直接启动轮询
                LiveViewActive  = true;
                _liveViewTimer  = new System.Timers.Timer(100);
                _liveViewTimer.Elapsed += (_, _) => RefreshLiveView();
                _liveViewTimer.Start();
                AddLog("实时取景已开启（10fps）", LogLevel.Success);
                RefreshStepStates();
            }
        }

        private void RefreshLiveView()
        {
            byte[]? jpeg = _camera.GetLiveViewJpeg();
            if (jpeg == null || jpeg.Length == 0) return;
            UI(() =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource    = new MemoryStream(jpeg);
                    bmp.CacheOption     = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    LiveViewImage = bmp;
                }
                catch { }
            });
        }

        // ── 角度轮询 ─────────────────────────────────────────────────
        private void StartAnglePolling()
        {
            _angleTimer = new System.Timers.Timer(300);
            _angleTimer.Elapsed += (_, _) =>
            {
                if (!PlcConnected) return;
                try
                {
                    double angle = _turntable.ReadAngle();
                    UI(() => CurrentAngle = angle);
                }
                catch { }
            };
            _angleTimer.Start();
        }

        private void StopAnglePolling()
        {
            _angleTimer?.Stop();
            _angleTimer?.Dispose();
            _angleTimer = null;
        }

        // ── 浏览文件夹 ───────────────────────────────────────────────
        private void BrowseFolder(object? _)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description  = "选择照片保存目录",
                SelectedPath = SaveFolder,
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                SaveFolder = dialog.SelectedPath;
        }

        // ── 转盘手动控制方法 ─────────────────────────────────────────
        private async Task WriteParamsAsync()
        {
            if (!PlcConnected) { AddLog("请先连接 PLC", LogLevel.Warning); return; }
            try
            {
                await Task.Run(() => _turntable.Configure(TurntableSpeed, TurntablePulse, TurntableAccelDecel));
                AddLog($"参数已写入：速度 {TurntableSpeed} / 脉冲 {TurntablePulse} / 加减速 {TurntableAccelDecel} ms", LogLevel.Success);
            }
            catch (Exception ex) { AddLog($"写入参数失败：{ex.Message}", LogLevel.Error); }
        }

        private async Task MoveForwardAsync()
        {
            if (!PlcConnected || IsCapturing) { AddLog("PLC 未连接或正在拍摄", LogLevel.Warning); return; }
            try
            {
                AddLog($"正转到 {TargetAngle:F1}°…", LogLevel.Info);
                await Task.Run(() => _turntable.MoveToWithDirectionAsync(TargetAngle, true));
                AddLog($"已到达 {TargetAngle:F1}°", LogLevel.Success);
            }
            catch (Exception ex) { AddLog($"正转失败：{ex.Message}", LogLevel.Error); }
        }

        private async Task MoveReverseAsync()
        {
            if (!PlcConnected || IsCapturing) { AddLog("PLC 未连接或正在拍摄", LogLevel.Warning); return; }
            try
            {
                AddLog($"反转到 {TargetAngle:F1}°…", LogLevel.Info);
                await Task.Run(() => _turntable.MoveToWithDirectionAsync(TargetAngle, false));
                AddLog($"已到达 {TargetAngle:F1}°", LogLevel.Success);
            }
            catch (Exception ex) { AddLog($"反转失败：{ex.Message}", LogLevel.Error); }
        }

        private async Task StartRotationAsync()
        {
            if (!PlcConnected) { AddLog("请先连接 PLC", LogLevel.Warning); return; }
            try
            {
                await Task.Run(() => { _turntable.SetDirection(IsForwardDirection); _turntable.StartRotation(); });
                AddLog($"转盘已启动（{(IsForwardDirection ? "正转" : "反转")}）", LogLevel.Success);
            }
            catch (Exception ex) { AddLog($"启动失败：{ex.Message}", LogLevel.Error); }
        }

        private void SafeStop()
        {
            if (!PlcConnected) return;
            try { _turntable.StopRotation(); AddLog("转盘已停止", LogLevel.Info); }
            catch (Exception ex) { AddLog($"停止失败：{ex.Message}", LogLevel.Error); }
        }

        private async Task GoHomeAsync()
        {
            if (!PlcConnected) { AddLog("请先连接 PLC", LogLevel.Warning); return; }
            var result = MessageBox.Show("确认执行回零操作？", "回零确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                await Task.Run(() => _turntable.GoHome());
                AddLog("回零指令已发送", LogLevel.Success);
            }
            catch (Exception ex) { AddLog($"回零失败：{ex.Message}", LogLevel.Error); }
        }

        private void ManualJog(bool forward)
        {
            if (!PlcConnected) return;
            try { _turntable.SetDirection(forward); _turntable.StartRotation(); }
            catch (Exception ex) { AddLog($"手动点动失败：{ex.Message}", LogLevel.Error); }
        }

        // ── 彩色日志追加 ─────────────────────────────────────────────
        private void AddLog(string message, LogLevel level)
        {
            UI(() =>
            {
                LogEntries.Insert(0, new LogEntry
                {
                    Time    = DateTime.Now.ToString("HH:mm:ss"),
                    Message = message,
                    Level   = level,
                });
                // 只保留最近 200 条
                while (LogEntries.Count > 200)
                    LogEntries.RemoveAt(LogEntries.Count - 1);
            });
        }

        private static void UI(Action action)
            => Application.Current?.Dispatcher.Invoke(action);

        // ── INotifyPropertyChanged ───────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _liveViewTimer?.Dispose();
            _angleTimer?.Dispose();
            _captureCts?.Dispose();
            _camera.Dispose();
            _turntable.Dispose();
        }
    }

    // ── RelayCommand ─────────────────────────────────────────────────
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private bool _running;
        public event EventHandler? CanExecuteChanged;
        public AsyncRelayCommand(Func<Task> execute) => _execute = execute;
        public bool CanExecute(object? _) => !_running;
        public async void Execute(object? _)
        {
            if (_running) return;
            _running = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try { await _execute(); }
            finally
            {
                _running = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public event EventHandler? CanExecuteChanged;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public bool CanExecute(object? _) => true;
        public void Execute(object? param) => _execute(param);
    }
}
