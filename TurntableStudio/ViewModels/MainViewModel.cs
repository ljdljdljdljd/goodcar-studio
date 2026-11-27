using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

    public sealed class ShotThumbnailItem
    {
        public int ShotNumber { get; set; }
        public string AngleText { get; set; } = string.Empty;
        public BitmapImage? Thumbnail { get; set; }
        public string NumberText => $"{ShotNumber:D3}";
    }

    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private readonly TurntableService _turntable;
        private readonly CameraService _camera = new();
        private readonly Func<bool>? _readPlcIdle;
        private readonly CaptureOrchestrator? _orchestrator;
        private CancellationTokenSource? _captureCts;
        private CancellationTokenSource? _manualMoveCts;
        private DispatcherTimer? _liveViewTimer;
        private DispatcherTimer? _angleTimer;
        private Task _liveViewPoll = Task.CompletedTask;
        private Task _anglePoll = Task.CompletedTask;
        private Task _deviceTask = Task.CompletedTask;
        private bool _deviceBusy;
        private bool _closing;
        private bool _manualMotionMayBeActive;
        private bool _anglePollFailed;

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
                OnPropertyChanged(nameof(CanUseManual));
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

        private double _leadAngle = 0.5;
        public double LeadAngle
        {
            get => _leadAngle;
            set => Set(ref _leadAngle, Math.Max(0.1, Math.Min(2.0, Math.Round(value, 1))));
        }

        private string _cameraModel = "未连接";
        public string CameraModel { get => _cameraModel; private set => Set(ref _cameraModel, value); }

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
                OnPropertyChanged(nameof(AngleStatusText));
            }
        }
        public string CurrentAngleText => $"{_currentAngle:F1}°";
        public double DialRotation => _currentAngle; // 供 XAML RotateTransform 使用
        public string AngleStatusText => PlcConnected ? $"{_currentAngle:F1}°" : "—";

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

        // ── 日志流（拆分为两个独立集合）────────────────────────────────
        public ObservableCollection<LogEntry> AutoCaptureLog { get; } = new();
        public ObservableCollection<LogEntry> TurntableLog { get; } = new();
        public ObservableCollection<LogEntry> LogEntries => AutoCaptureLog;  // 兼容别名

        // ── 缩略图预览 ───────────────────────────────────────────────
        public ObservableCollection<ShotThumbnailItem> ShotThumbnails { get; } = new();

        private ShotThumbnailItem? _latestShot;
        public ShotThumbnailItem? LatestShot
        {
            get => _latestShot;
            private set => Set(ref _latestShot, value);
        }

        // ── 转盘手动控制属性 ────────────────────────────────────────────
        private ushort _turntableSpeed = 230;
        public ushort TurntableSpeed { get => _turntableSpeed; set => Set(ref _turntableSpeed, value); }

        private ushort _turntablePulse = 24690;
        public ushort TurntablePulse { get => _turntablePulse; set => Set(ref _turntablePulse, value); }

        private ushort _turntableAccelDecel = 1000;
        public ushort TurntableAccelDecel { get => _turntableAccelDecel; set => Set(ref _turntableAccelDecel, value); }

        private bool _isForwardDirection = true;
        public bool IsForwardDirection { get => _isForwardDirection; set => Set(ref _isForwardDirection, value); }

        private double _targetAngle = 90;
        public double TargetAngle { get => _targetAngle; set => Set(ref _targetAngle, value); }

        // ── 守卫 ─────────────────────────────────────────────────────
        public bool CanStartCapture => !_closing && (IsCapturing ||
            (!_deviceBusy && !_manualMotionMayBeActive && PlcConnected && CameraConnected));
        public bool CanUseManual => !_closing && !_deviceBusy && !_manualMotionMayBeActive && PlcConnected;

        // ── 命令 ─────────────────────────────────────────────────────
        public ICommand ConnectPlcCommand    { get; }
        public ICommand ConnectCameraCommand { get; }
        public ICommand StartStopCommand     { get; }
        public ICommand ToggleLiveViewCommand{ get; }
        public ICommand BrowseFolderCommand  { get; }
        public ICommand WriteParamsCommand   { get; }
        public ICommand StartRotationCommand { get; }
        public ICommand StopRotationCommand  { get; }
        public ICommand GoHomeCommand        { get; }
        public ICommand ManualForwardCommand { get; }
        public ICommand ManualReverseCommand   { get; }
        public ICommand MoveForwardCommand     { get; }
        public ICommand MoveReverseCommand     { get; }
        public ICommand ReconnectCameraCommand { get; }

        public MainViewModel() : this(null) { }

        public MainViewModel(Func<bool>? readPlcIdle) : this(new TurntableService(), readPlcIdle) { }

        internal MainViewModel(TurntableService turntable, Func<bool>? readPlcIdle)
        {
            _turntable = turntable;
            _readPlcIdle = readPlcIdle;
            _turntable.StatusChanged += (_, msg) => AddLog(msg, LogLevel.Info, turntable: false);

            if (_turntable.Controller != null && _camera != null)
            {
                try
                {
                    _orchestrator = new CaptureOrchestrator(_turntable.Controller, null!);
                }
                catch { /* CaptureJob 初始化依赖 UI 线程，构造时 Controller 可能未连接 */ }
            }

            ConnectPlcCommand       = new AsyncRelayCommand(() => RunDeviceAsync(ConnectPlcAsync));
            ConnectCameraCommand    = new AsyncRelayCommand(() => RunDeviceAsync(ConnectCameraAsync));
            ReconnectCameraCommand  = new AsyncRelayCommand(() => RunDeviceAsync(ReconnectCameraAsync));
            StartStopCommand        = new RelayCommand(_ => StartOrCancelCapture());
            ToggleLiveViewCommand   = new AsyncRelayCommand(() => RunDeviceAsync(ToggleLiveViewAsync));
            BrowseFolderCommand     = new RelayCommand(BrowseFolder);
            WriteParamsCommand      = new AsyncRelayCommand(() => RunDeviceAsync(WriteParamsAsync));
            StartRotationCommand    = new AsyncRelayCommand(() => RunDeviceAsync(() => StartRotationAsync(IsForwardDirection)));
            StopRotationCommand     = new AsyncRelayCommand(StopOrCancelAsync);
            GoHomeCommand           = new AsyncRelayCommand(() => RunDeviceAsync(GoHomeAsync));
            ManualForwardCommand    = new AsyncRelayCommand(() => RunDeviceAsync(() => StartRotationAsync(true)));
            ManualReverseCommand    = new AsyncRelayCommand(() => RunDeviceAsync(() => StartRotationAsync(false)));
            MoveForwardCommand      = new AsyncRelayCommand(() => MoveToAngleAsync(true));
            MoveReverseCommand      = new AsyncRelayCommand(() => MoveToAngleAsync(false));
        }

        private void RefreshDeviceGuards()
        {
            OnPropertyChanged(nameof(CanStartCapture));
            OnPropertyChanged(nameof(CanUseManual));
        }

        private Task RunDeviceAsync(Func<Task> action, bool stopping = false)
        {
            if (_closing || _deviceBusy || (_manualMotionMayBeActive && !stopping))
                return Task.CompletedTask;
            _deviceBusy = true;
            RefreshDeviceGuards();
            return _deviceTask = RunDeviceCoreAsync(action);
        }

        private async Task RunDeviceCoreAsync(Func<Task> action)
        {
            try
            {
                await Task.Yield();
                await _anglePoll;
                if (!_closing) await action();
            }
            catch (Exception ex) { AddLog(ex.ToString(), LogLevel.Error); }
            finally
            {
                _deviceBusy = false;
                RefreshDeviceGuards();
            }
        }

        // ── 连接 PLC ─────────────────────────────────────────────────
        private async Task ConnectPlcAsync()
        {
            if (PlcConnected)
            {
                await StopAnglePollingAsync();
                await Task.Run(() => _turntable.Disconnect());
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
                await StopLiveViewAsync();
                try { await Task.Run(() => _camera.Disconnect()); }
                finally { CameraConnected = false; }
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
                    _camera.Disconnect();
                    // 新 ABI：Initialize(sdkRoot) → GetDeviceDescriptor(index) → Connect(descriptor, interfaceType)
                    // sdkRoot 为 null 时 bridge 使用程序目录（Cr_Core.dll 与 SonyCrBridge.dll 同目录）
                    _camera.Initialize(null);
                    // 枚举设备：SDK Initialize 完成后设备列表为空，必须先触发枚举再读取
                    int count = _camera.GetDeviceCount();
                    if (count == 0)
                        throw new InvalidOperationException("未发现相机，请确认 USB 已连接且相机处于 PC Remote 模式");
                    desc = _camera.GetDeviceDescriptor(0);
                    _camera.Connect(desc, interfaceType: 0); // 0 = USB
                    count = 1;
                });
                CameraConnected = true;
                CameraModel = "Sony α7R IV";  // TODO: 从 SDK descriptor JSON 提取 model 字段
                AddLog($"相机连接成功（共 {count} 台）", LogLevel.Success);
                AddLog($"设备：{desc}", LogLevel.Info);
            }
            catch (Exception ex) { AddLog($"相机连接失败：{ex.Message}", LogLevel.Error); }
        }

        private async Task ReconnectCameraAsync()
        {
            await StopLiveViewAsync();
            try
            {
                await Task.Run(() => _camera.Disconnect());
                CameraConnected = false;
                AddLog("相机已断开", LogLevel.Warning);
            }
            catch { }
            await ConnectCameraAsync();
        }

        // ── 开始 / 停止拍摄 ──────────────────────────────────────────
        private void StartOrCancelCapture()
        {
            if (IsCapturing)
            {
                CancelCapture();
                return;
            }
            if (CanStartCapture) _ = StartCaptureWithLifetimeAsync();
        }

        private async Task StartCaptureWithLifetimeAsync()
        {
            using var captureCts = new CancellationTokenSource();
            _captureCts = captureCts;
            IsCapturing = true;
            try { await RunDeviceAsync(() => StartCaptureAsync(captureCts.Token)); }
            finally
            {
                _captureCts = null;
                IsCapturing = false;
                RefreshStepStates();
            }
        }

        private void CancelCapture()
        {
            if (_captureCts == null || _captureCts.IsCancellationRequested) return;
            _captureCts.Cancel();
            AddLog("正在停止拍摄…", LogLevel.Warning);
        }

        private async Task StartCaptureAsync(CancellationToken ct)
        {
            if (!PlcConnected || !CameraConnected)
            {
                AddLog("请先连接 PLC 与相机", LogLevel.Warning); return;
            }
            if (string.IsNullOrWhiteSpace(SaveFolder))
            {
                AddLog("请先指定保存目录", LogLevel.Warning); return;
            }

            if (_orchestrator == null)
            {
                AddLog("编排器未初始化", LogLevel.Error);
                return;
            }

            UI(() => ShotThumbnails.Clear());
            ProgressCurrent = 0;
            ProgressTotal   = SelectedShotCount;

            _orchestrator.Progress += OnCaptureProgress;
            _orchestrator.Log += OnCaptureLog;

            void OnCaptureProgress(object? sender, CaptureProgressEventArgs e)
            {
                UI(() =>
                {
                    ProgressCurrent = e.Current;
                    ProgressTotal   = e.Total;
                    if (!string.IsNullOrEmpty(e.FileName))
                    {
                        string fullPath = Path.Combine(SaveFolder, e.FileName);
                        double angle = double.TryParse(e.ActualAngleText.TrimEnd('°'), out var a) ? a : 0;
                        AddShotThumbnail(e.Current, angle, fullPath);
                    }
                });
            }

            void OnCaptureLog(object? sender, string msg)
            {
                var level = msg.StartsWith("✓") ? LogLevel.Success
                           : msg.StartsWith("✗") ? LogLevel.Error
                           : msg.StartsWith("⚠") ? LogLevel.Warning
                           : LogLevel.Info;
                AddLog(msg, level);
            }

            try
            {
                await StopLiveViewAsync();
                ct.ThrowIfCancellationRequested();
                int count = SelectedShotCount;
                string folder = SaveFolder;
                AddLog($"开始精拍 {count} 张 → {folder}", LogLevel.Info);
                await _orchestrator.RunAsync(count, folder, _leadAngle, ct);
                AddLog($"拍摄完成，共 {ProgressCurrent} 张", LogLevel.Success);
            }
            finally
            {
                _orchestrator.Progress -= OnCaptureProgress;
                _orchestrator.Log -= OnCaptureLog;
            }
            catch (OperationCanceledException) { AddLog("拍摄已取消", LogLevel.Warning); }
            catch (AggregateException ex)
            {
                _manualMotionMayBeActive = true;
                AddLog($"拍摄出错：{ex}", LogLevel.Error);
            }
            catch (Exception ex) { AddLog($"拍摄出错：{ex.Message}", LogLevel.Error); }
        }

        // ── 实时取景 ─────────────────────────────────────────────────
        private async Task ToggleLiveViewAsync()
        {
            if (!CameraConnected) { AddLog("请先连接相机", LogLevel.Warning); return; }

            if (LiveViewActive)
            {
                await StopLiveViewAsync();
                AddLog("实时取景已关闭", LogLevel.Info);
            }
            else
            {
                LiveViewActive  = true;
                _liveViewTimer  = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _liveViewTimer.Tick += (_, _) =>
                {
                    if (!_closing && !_deviceBusy && _liveViewPoll.IsCompleted)
                        _liveViewPoll = RefreshLiveViewAsync();
                };
                _liveViewTimer.Start();
                AddLog("实时取景已开启（10fps）", LogLevel.Success);
                RefreshStepStates();
            }
        }

        private async Task RefreshLiveViewAsync()
        {
            try
            {
                byte[]? jpeg = await Task.Run(() => _camera.GetLiveViewJpeg());
                if (_closing || !LiveViewActive || jpeg == null || jpeg.Length == 0) return;
                using var stream = new MemoryStream(jpeg);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = stream;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                LiveViewImage = bmp;
            }
            catch (InvalidOperationException ex)
            {
                _liveViewTimer?.Stop();
                _liveViewTimer = null;
                LiveViewActive = false;
                LiveViewImage = null;
                CameraConnected = false;
                AddLog($"实时取景停止：{ex.Message}", LogLevel.Error);
            }
            catch (Exception ex) { AddLog($"取景帧无效：{ex.Message}", LogLevel.Warning); }
        }

        private async Task StopLiveViewAsync()
        {
            _liveViewTimer?.Stop();
            _liveViewTimer = null;
            LiveViewActive = false;
            await _liveViewPoll;
            LiveViewImage = null;
            RefreshStepStates();
        }

        // ── 角度轮询 ─────────────────────────────────────────────────
        private void StartAnglePolling()
        {
            _angleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _angleTimer.Tick += (_, _) =>
            {
                if (!_closing && PlcConnected && _anglePoll.IsCompleted)
                    _anglePoll = PollAngleAsync();
            };
            _angleTimer.Start();
        }

        private async Task PollAngleAsync()
        {
            try
            {
                CurrentAngle = await Task.Run(() => _turntable.ReadAngle());
                _anglePollFailed = false;
            }
            catch (Exception ex)
            {
                if (!_anglePollFailed) AddLog($"角度回读失败：{ex.Message}", LogLevel.Error);
                _anglePollFailed = true;
            }
        }

        private async Task StopAnglePollingAsync()
        {
            _angleTimer?.Stop();
            _angleTimer = null;
            await _anglePoll;
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
            if (!PlcConnected) { AddLog("请先连接 PLC", LogLevel.Warning, turntable: true); return; }
            try
            {
                await Task.Run(() => _turntable.Configure(TurntableSpeed, TurntablePulse, TurntableAccelDecel));
                AddLog($"参数已写入：速度 {TurntableSpeed} / 脉冲 {TurntablePulse} / 加减速 {TurntableAccelDecel} ms", LogLevel.Success, turntable: true);
            }
            catch (Exception ex) { AddLog($"写入参数失败：{ex.Message}", LogLevel.Error, turntable: true); }
        }

        private async Task StartRotationAsync(bool forward)
        {
            if (!PlcConnected) { AddLog("请先连接 PLC", LogLevel.Warning, turntable: true); return; }
            _manualMotionMayBeActive = true;
            try
            {
                await Task.Run(() => { _turntable.SetDirection(forward); _turntable.StartRotation(); });
                AddLog($"已发送{(forward ? "正转" : "反转")}启动指令", LogLevel.Success, turntable: true);
            }
            catch (Exception ex) { AddLog($"启动失败：{ex.Message}", LogLevel.Error, turntable: true); }
        }

        private async Task StopOrCancelAsync()
        {
            if (IsCapturing) { CancelCapture(); return; }
            var manualMove = _manualMoveCts;
            manualMove?.Cancel();
            if (!PlcConnected) return;
            if (_deviceBusy) await _deviceTask;
            if (IsCapturing) { CancelCapture(); return; }
            if (manualMove != null && !_manualMotionMayBeActive) return;
            await RunDeviceAsync(StopManualAsync, stopping: true);
        }

        private async Task StopManualAsync()
        {
            _manualMotionMayBeActive = true;
            await Task.Run(() => _turntable.StopRotation());
            _manualMotionMayBeActive = false;
            AddLog("已发送转盘停止指令。", LogLevel.Info, turntable: true);
        }

        private async Task GoHomeAsync()
        {
            if (!PlcConnected) { AddLog("请先连接 PLC", LogLevel.Warning); return; }
            var result = MessageBox.Show("确认执行回零操作？", "回零确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes || _closing) return;
            try
            {
                _manualMotionMayBeActive = true;
                await Task.Run(() => _turntable.GoHome());
                AddLog("回零指令已发送", LogLevel.Success, turntable: true);
            }
            catch (Exception ex) { AddLog($"回零失败：{ex.Message}", LogLevel.Error, turntable: true); }
        }

        private async Task MoveToAngleAsync(bool forward)
        {
            if (_manualMoveCts != null) return;
            if (!PlcConnected) { AddLog("请先连接 PLC", LogLevel.Warning); return; }
            double targetAngle = TargetAngle;
            if (!(targetAngle >= 0 && targetAngle < 360))
            {
                AddLog("目标角度必须在 0-360° 范围内", LogLevel.Warning);
                return;
            }

            using var cts = new CancellationTokenSource();
            _manualMoveCts = cts;
            try { await RunDeviceAsync(MoveAsync); }
            finally { _manualMoveCts = null; }

            async Task MoveAsync()
            {
                _manualMotionMayBeActive = true;
                try
                {
                    await Task.Run(() => _turntable.MoveToAsync(targetAngle, forward, toleranceDeg: 1.0,
                        timeoutMs: 30000, ct: cts.Token));
                    _manualMotionMayBeActive = false;
                    AddLog($"已到达目标角度 {targetAngle:F1}°（{(forward ? "正转" : "反转")}）", LogLevel.Success, turntable: true);
                }
                catch (TimeoutException ex)
                {
                    _manualMotionMayBeActive = false;
                    AddLog($"移动超时：{ex.Message}", LogLevel.Error, turntable: true);
                }
                catch (OperationCanceledException)
                {
                    _manualMotionMayBeActive = false;
                    AddLog("移动已取消", LogLevel.Warning, turntable: true);
                }
                catch (Exception ex) { AddLog($"移动到目标角度失败：{ex.Message}", LogLevel.Error, turntable: true); }
            }
        }

        // ── 彩色日志追加（支持双流拆分）─────────────────────────────────
        private void AddLog(string message, LogLevel level, bool turntable = false)
        {
            UI(() =>
            {
                var entry = new LogEntry
                {
                    Time    = DateTime.Now.ToString("HH:mm:ss"),
                    Message = message,
                    Level   = level,
                };
                var target = turntable ? TurntableLog : AutoCaptureLog;
                target.Insert(0, entry);
                while (target.Count > 300)
                    target.RemoveAt(target.Count - 1);
            });
        }

        // ── 缩略图追加 ───────────────────────────────────────────────
        public void AddShotThumbnail(int shotNumber, double angle, string filePath)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(filePath, UriKind.Absolute);
                bmp.DecodePixelWidth = 160;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                var item = new ShotThumbnailItem
                {
                    ShotNumber = shotNumber,
                    AngleText = $"{angle:F1}°",
                    Thumbnail = bmp
                };
                UI(() =>
                {
                    ShotThumbnails.Add(item);
                    LatestShot = item;
                });
            }
            catch { /* 缩略图加载失败不影响拍摄 */ }
        }

        private static void UI(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }

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

        public async Task ShutdownAsync()
        {
            _closing = true;
            RefreshDeviceGuards();
            CancelCapture();
            _manualMoveCts?.Cancel();
            try
            {
                await _deviceTask;
                await StopLiveViewAsync();
                await StopAnglePollingAsync();
                if (_manualMotionMayBeActive) await StopManualAsync();
                await Task.Run(() => _camera.Dispose());
                CameraConnected = false;
                await Task.Run(() => _turntable.Dispose());
                PlcConnected = false;
            }
            catch
            {
                _closing = false;
                RefreshDeviceGuards();
                throw;
            }
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
