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
        public string ActualAngleText { get; set; } = string.Empty;
    }

    /// <summary>
    /// v4 联动拍摄编排器：使用华腾 SDK CaptureJob.StartOneClick 实现停稳逻辑。
    /// </summary>
    public sealed class CaptureOrchestrator : IDisposable
    {
        private readonly CaptureJob _job;

        public event EventHandler<CaptureProgressEventArgs>? Progress;
        public event EventHandler<string>? Log;
        public event EventHandler<bool>? Finished;  // bool cancelled

        /// <summary>
        /// 构造时订阅 SDK 事件。必须在 UI 线程实例化（SDK §3.2）。
        /// </summary>
        public CaptureOrchestrator(TurntableController controller, CameraConnection camera)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            if (camera == null) throw new ArgumentNullException(nameof(camera));

            _job = new CaptureJob(controller, camera);

            _job.Progress += (sender, e) =>
            {
                Progress?.Invoke(this, new CaptureProgressEventArgs
                {
                    Current = e.ShotIndex,
                    Total = e.ShotCount,
                    FileName = e.LastFile ?? string.Empty,
                    ActualAngleText = $"{e.TraveledAngle:F1}°"
                });
                Log?.Invoke(this, $"  [{e.ShotIndex}/{e.ShotCount}] {e.LastFile} @ {e.TraveledAngle:F1}°");
            };

            _job.Error += (sender, e) =>
            {
                Log?.Invoke(this, $"✗ SDK 错误：{e}");
            };

            _job.Finished += (sender, e) =>
            {
                if (e.Cancelled)
                    Log?.Invoke(this, "⚠ 拍摄已取消");
                else
                    Log?.Invoke(this, $"✓ 拍摄完成，共 {e.Saved}/{e.Saved + e.Skipped} 张");
                Finished?.Invoke(this, e.Cancelled);
            };
        }

        /// <summary>
        /// 一键联动拍摄，停稳逻辑由 SDK 内部处理。
        /// </summary>
        public Task RunAsync(int shotCount, string saveFolder,
            double leadAngle = 0.5, CancellationToken ct = default)
        {
            if (shotCount != 36 && shotCount != 72)
                throw new ArgumentOutOfRangeException(nameof(shotCount), "仅支持 36 或 72 张");
            if (string.IsNullOrEmpty(saveFolder))
                throw new ArgumentException("保存目录不能为空", nameof(saveFolder));
            if (leadAngle < 0.1 || leadAngle > 2.0)
                throw new ArgumentOutOfRangeException(nameof(leadAngle), "提前触发量范围：0.1~2.0°");

            var settings = CaptureJobSettings.ForFullCircle(shotCount);
            settings.LeadAngle = leadAngle;
            settings.SaveFolder = saveFolder;
            settings.FilePrefix = "Shot";

            var tcs = new TaskCompletionSource<bool>();

            void OnFinished(object? sender, CaptureJobResultEventArgs e)
            {
                _job.Finished -= OnFinished;
                _job.Error   -= OnError;
                if (e.Cancelled) tcs.TrySetCanceled();
                else tcs.TrySetResult(true);
            }

            void OnError(object? sender, string msg)
            {
                _job.Error   -= OnError;
                _job.Finished -= OnFinished;
                tcs.TrySetException(new InvalidOperationException(msg));
            }

            _job.Finished += OnFinished;
            _job.Error    += OnError;

            ct.Register(() =>
            {
                try { _job.Cancel(); }
                catch { }
            });

            try
            {
                _job.StartOneClick(settings);
            }
            catch (Exception ex)
            {
                _job.Finished -= OnFinished;
                _job.Error -= OnError;
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }

        public void Cancel() => _job.Cancel();

        public void Dispose() => _job.Dispose();
    }
}
