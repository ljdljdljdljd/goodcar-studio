using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TurntableStudio.Services;

namespace TurntableStudio.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly TurntableService _turntable = new();
        private readonly CameraService _camera = new();
        private CaptureOrchestrator? _orchestrator;
        private CancellationTokenSource? _captureCts;
        private System.Timers.Timer? _liveViewTimer;

        // ── 状态属性 ────────────────────────────────────────────────
        private bool _plcConnected;
        public bool PlcConnected { get => _plcConnected; private set => Set(ref _plcConnected, value); }

        private bool _cameraConnected;
        public bool CameraConnected { get => _cameraConnected; private set => Set(ref _cameraConnected, value); }

        private bool _isCapturing;
        public bool IsCapturing { get => _isCapturing; private set { Set(ref _isCapturing, value); OnPropertyChanged(nameof(StartStopLabel)); } }

        private bool _liveViewActive;
        public bool LiveViewActive { get => _liveViewActive; private set => Set(ref _liveViewActive, value); }

        // ── 配置属性 ────────────────────────────────────────────────
        private string _plcIp = "192.168.1.111";
        public string PlcIp { get => _plcIp; set => Set(ref _plcIp, value); }

        private string _saveFolder = @"D:\GoodCar\shots";
        public string SaveFolder { get => _saveFolder; set => Set(ref _saveFolder, value); }

        private int _selectedShotCount = 72;
        public int SelectedShotCount
        {
            get => _selectedShotCount;
            set { if (!IsCapturing) Set(ref _selectedShotCount, value); }
        }

        public ObservableCollection<int> ShotCountOptions { get; } = new() { 72, 36 };

        // ── 进度属性 ────────────────────────────────────────────────
        private int _progressCurrent;
        public int ProgressCurrent { get => _progressCurrent; private set { Set(ref _progressCurrent, value); OnPropertyChanged(nameof(ProgressText)); } }

        private int _progressTotal;
        public int ProgressTotal { get => _progressTotal; private set { Set(ref _progressTotal, value); OnPropertyChanged(nameof(ProgressText)); } }

        public string ProgressText => _progressTotal > 0 ? $"{_progressCurrent} / {_progressTotal}" : "—";
        public string StartStopLabel => IsCapturing ? "停止拍摄" : "开始拍摄";

        // ── 实时取景 ────────────────────────────────────────────────
        private BitmapImage? _liveViewImage;
        public BitmapImage? LiveViewImage { get => _liveViewImage; private set => Set(ref _liveViewImage, value); }

        // ── 日志 ────────────────────────────────────────────────────
        private string _log = string.Empty;
        public string Log { get => _log; private set => Set(ref _log, value); }

        // ── 命令 ────────────────────────────────────────────────────
        public ICommand ConnectPlcCommand { get; }
        public ICommand ConnectCameraCommand { get; }
        public ICommand StartStopCommand { get; }
        public ICommand ToggleLiveViewCommand { get; }
        public ICommand BrowseFolderCommand { get; }

        public MainViewModel()
        {
            _turntable.StatusChanged += (_, msg) => AppendLog(msg);

            ConnectPlcCommand = new AsyncRelayCommand(ConnectPlcAsync);
            ConnectCameraCommand = new AsyncRelayCommand(ConnectCameraAsync);
            StartStopCommand = new AsyncRelayCommand(StartStopAsync);
            ToggleLiveViewCommand = new AsyncRelayCommand(ToggleLiveViewAsync);
            BrowseFolderCommand = new RelayCommand(BrowseFolder);
        }

        private async Task ConnectPlcAsync()
        {
            try
            {
                if (PlcConnected)
                {
                    _turntable.Disconnect();
                    PlcConnected = false;
                    AppendLog("PLC 已断开");
                }
                else
                {
                    AppendLog($"正在连接 PLC {PlcIp}…");
                    await Task.Run(() => _turntable.Connect(PlcIp));
                    PlcConnected = true;
                    AppendLog("✓ PLC 已连接");
                }
            }
            catch (Exception ex) { AppendLog($"✗ PLC 连接失败：{ex.Message}"); }
        }

        private async Task ConnectCameraAsync()
        {
            try
            {
                if (CameraConnected)
                {
                    _camera.Disconnect();
                    CameraConnected = false;
                    AppendLog("相机已断开");
                }
                else
                {
                    AppendLog("正在初始化相机 SDK…");
                    await Task.Run(() =>
                    {
                        _camera.Initialize();
                        int count = _camera.GetDeviceCount();
                        if (count == 0) throw new InvalidOperationException("未发现已连接的索尼相机，请检查 USB 连接与 PC 遥控设置");
                        AppendLog($"发现 {count} 台相机，正在连接…");
                        string desc = _camera.GetDeviceDescriptor(0);
                        AppendLog($"  设备信息：{desc}");
                        _camera.Connect(0);
                    });
                    CameraConnected = true;
                    AppendLog("✓ 相机已连接");
                }
            }
            catch (Exception ex) { AppendLog($"✗ 相机连接失败：{ex.Message}"); }
        }

        private async Task StartStopAsync()
        {
            if (IsCapturing)
            {
                _captureCts?.Cancel();
                AppendLog("⏹ 用户停止拍摄");
                return;
            }

            if (!PlcConnected || !CameraConnected)
            {
                AppendLog("⚠ 请先连接 PLC 与相机");
                return;
            }

            if (string.IsNullOrWhiteSpace(SaveFolder))
            {
                AppendLog("⚠ 请指定保存目录");
                return;
            }

            _captureCts = new CancellationTokenSource();
            IsCapturing = true;
            ProgressCurrent = 0;
            ProgressTotal = SelectedShotCount;

            _orchestrator = new CaptureOrchestrator(_turntable, _camera);
            _orchestrator.Progress += (_, e) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ProgressCurrent = e.Current;
                    ProgressTotal = e.Total;
                });
            };
            _orchestrator.Log += (_, msg) => AppendLog(msg);

            try
            {
                AppendLog($"▶ 开始精拍：{SelectedShotCount} 张 → {SaveFolder}");
                await _orchestrator.RunAsync(SelectedShotCount, SaveFolder,
                    "Shot", _captureCts.Token);
                AppendLog($"✅ 拍摄完成，共 {ProgressCurrent} 张");
            }
            catch (OperationCanceledException)
            {
                AppendLog("⏹ 拍摄已取消");
            }
            catch (Exception ex)
            {
                AppendLog($"✗ 拍摄出错：{ex.Message}");
            }
            finally
            {
                IsCapturing = false;
                _captureCts?.Dispose();
                _captureCts = null;
            }
        }

        private async Task ToggleLiveViewAsync()
        {
            if (!CameraConnected) { AppendLog("⚠ 请先连接相机"); return; }

            if (LiveViewActive)
            {
                _liveViewTimer?.Stop();
                _liveViewTimer?.Dispose();
                _liveViewTimer = null;
                _camera.StopLiveView();
                LiveViewActive = false;
                LiveViewImage = null;
                AppendLog("实时取景已关闭");
            }
            else
            {
                await Task.Run(() => _camera.StartLiveView());
                LiveViewActive = true;
                _liveViewTimer = new System.Timers.Timer(100); // 10fps
                _liveViewTimer.Elapsed += (_, _) => RefreshLiveView();
                _liveViewTimer.Start();
                AppendLog("实时取景已开启");
            }
        }

        private void RefreshLiveView()
        {
            byte[]? jpeg = _camera.GetLiveViewJpeg(500);
            if (jpeg == null || jpeg.Length == 0) return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(jpeg);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    LiveViewImage = bmp;
                }
                catch { /* 丢帧，不抛出 */ }
            });
        }

        private void BrowseFolder(object? _)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择照片保存目录",
                SelectedPath = SaveFolder,
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                SaveFolder = dialog.SelectedPath;
        }

        private void AppendLog(string message)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                Log = $"[{DateTime.Now:HH:mm:ss}] {message}\n{Log}";
            });
        }

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
            _liveViewTimer?.Dispose();
            _captureCts?.Dispose();
            _camera.Dispose();
            _turntable.Dispose();
        }
    }

    // ── 轻量 RelayCommand 实现 ────────────────────────────────────
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private bool _running;
        public event EventHandler? CanExecuteChanged;
        public AsyncRelayCommand(Func<Task> execute) => _execute = execute;
        public bool CanExecute(object? _) => !_running;
        public async void Execute(object? _)
        {
            _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try { await _execute(); }
            finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
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
