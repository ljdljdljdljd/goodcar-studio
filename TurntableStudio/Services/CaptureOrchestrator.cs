using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TurntableStudio.Services
{
    public sealed class ShotRecord
    {
        public int Index { get; set; }
        public double TargetAngle { get; set; }
        public double ActualAngle { get; set; }
        public string FileName { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public long ElapsedMs { get; set; }
    }

    public sealed class CaptureProgressEventArgs : EventArgs
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string FileName { get; set; } = string.Empty;
    }

    public sealed class CaptureOrchestrator
    {
        private const int MaxAttempts = 3;
        private const long MinFileSizeBytes = 20480;
        private const int FileWaitTimeoutMs = 30000;
        private const int MotionStartTimeoutMs = 5000;
        private const int MotionSettleTimeoutMs = 30000;
        private const int SampleIntervalMs = 100;
        private const int StableWindowMs = 500;
        private const int MaxSampleGapMs = 300;
        private const double TargetToleranceDeg = 0.5;

        private readonly TurntableService _turntable;
        private readonly CameraService _camera;
        private readonly Func<bool>? _readPlcIdle;

        public event EventHandler<CaptureProgressEventArgs>? Progress;
        public event EventHandler<string>? Log;

        public CaptureOrchestrator(TurntableService turntable, CameraService camera, Func<bool>? readPlcIdle = null)
        {
            _turntable = turntable;
            _camera = camera;
            _readPlcIdle = readPlcIdle;
        }

        public async Task RunAsync(int shotCount, string saveFolder,
            string filePrefix = "Shot", CancellationToken ct = default)
        {
            if (shotCount != 36 && shotCount != 72)
                throw new ArgumentOutOfRangeException(nameof(shotCount));
            if (string.IsNullOrEmpty(filePrefix) || filePrefix.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_' && ch != '-'))
                throw new ArgumentException("文件名前缀只能含字母、数字、下划线和连字符。", nameof(filePrefix));
            if (_readPlcIdle == null)
                throw new InvalidOperationException("未配置已验收的 PLC 停稳反馈；禁止联动启动。");

            ct.ThrowIfCancellationRequested();
            _camera.EnsureCaptureReady();
            string runFolder = Path.Combine(Path.GetFullPath(saveFolder), $"run_{Guid.NewGuid():N}");
            Directory.CreateDirectory(runFolder);
            Log?.Invoke(this, $"本轮保存目录：{runFolder}");
            var records = new List<ShotRecord>(shotCount);

            try
            {
                double stepAngle = 360.0 / shotCount;
                double initialAngle = await WaitForRestAsync(null, ct);
                double previousAngle = initialAngle;
                for (int index = 1; index <= shotCount; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    double targetAngle = (initialAngle + index * stepAngle) % 360.0;
                    _turntable.SetDirection(true);
                    ct.ThrowIfCancellationRequested();
                    var motion = Stopwatch.StartNew();
                    _turntable.StartRotation();
                    await WaitForMotionAsync(previousAngle, motion, ct);
                    await WaitAndStopAsync(previousAngle, stepAngle, ct);
                    double actualAngle = await WaitForRestAsync(targetAngle, ct);
                    if (Math.Abs(SignedDelta(previousAngle, actualAngle) - stepAngle) > TargetToleranceDeg)
                        throw new InvalidOperationException($"第 {index} 步实际步幅不符：{previousAngle:F1}° → {actualAngle:F1}°。");

                    var record = await CaptureFrameAsync(runFolder, filePrefix, index, targetAngle, actualAngle, ct);
                    records.Add(record);
                    previousAngle = actualAngle;
                    Progress?.Invoke(this, new CaptureProgressEventArgs
                    {
                        Current = index, Total = shotCount, FileName = record.FileName
                    });
                }

                if (records.Count != shotCount)
                    throw new IOException($"本轮仅保存 {records.Count}/{shotCount} 张。");
                await WriteManifestAsync(runFolder, shotCount, records, ct);
                Log?.Invoke(this, $"拍摄完成，共 {records.Count} 张，manifest.json 已生成。");
            }
            catch (Exception failure)
            {
                try
                {
                    _turntable.StopRotation();
                    await WaitForRestAsync(null, CancellationToken.None);
                }
                catch (Exception stopFailure)
                {
                    throw new AggregateException("联动已中断，但无法确认转盘停稳。", failure, stopFailure);
                }
                throw;
            }
        }

        private static double SignedDelta(double from, double to) => (to - from + 540.0) % 360.0 - 180.0;

        private double ReadAngle()
        {
            double angle = _turntable.ReadAngle();
            if (double.IsNaN(angle) || angle < 0 || angle >= 360)
                throw new IOException($"无效的 PLC 角度读数：{angle}。");
            return angle;
        }

        private async Task WaitAndStopAsync(double previousAngle, double stepAngle, CancellationToken ct)
        {
            // 轮询角度，当已转过步幅的 80% 时发 StopRotation
            // 防止转盘冲过目标太多
            double stopThreshold = stepAngle * 0.80;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < MotionSettleTimeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                double current = ReadAngle();
                double traveled = SignedDelta(previousAngle, current);
                if (traveled >= stopThreshold)
                {
                    _turntable.StopRotation();
                    return;
                }
                await Task.Delay(SampleIntervalMs, ct);
            }
            // 超时仍未到 80%，强制停止
            _turntable.StopRotation();
        }

        private async Task WaitForMotionAsync(double before, Stopwatch clock, CancellationToken ct)
        {
            while (clock.ElapsedMilliseconds < MotionStartTimeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                double delta = SignedDelta(before, ReadAngle());
                ct.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds >= MotionStartTimeoutMs) break;
                if (delta <= -1) throw new InvalidOperationException("转盘运动方向与正转不符。");
                if (delta >= 1) return;
                await Task.Delay(SampleIntervalMs, ct);
            }
            throw new TimeoutException($"在 {MotionStartTimeoutMs}ms 内未确认正向步进。");
        }

        private async Task<double> WaitForRestAsync(double? targetAngle, CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            long lastSample = 0;
            long stableSince = -1;
            double anchor = 0;
            while (clock.ElapsedMilliseconds < MotionSettleTimeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                double angle = ReadAngle();
                bool idle = _readPlcIdle!();
                ct.ThrowIfCancellationRequested();
                long now = clock.ElapsedMilliseconds;
                if (now >= MotionSettleTimeoutMs) break;
                if (!idle) stableSince = -1;
                else if (stableSince < 0 || now - lastSample > MaxSampleGapMs || angle != anchor)
                {
                    stableSince = now;
                    anchor = angle;
                }
                else if (now - stableSince >= StableWindowMs)
                {
                    if (targetAngle.HasValue && Math.Abs(SignedDelta(targetAngle.Value, angle)) > TargetToleranceDeg)
                        throw new InvalidOperationException($"转盘已停但角度不符：目标 {targetAngle:F1}°，实测 {angle:F1}°。请检查 PLC 步进模式。");
                    return angle;
                }
                lastSample = now;
                await Task.Delay(SampleIntervalMs, ct);
            }
            throw new TimeoutException($"在 {MotionSettleTimeoutMs}ms 内未确认转盘停稳。");
        }

        private async Task<ShotRecord> CaptureFrameAsync(string runFolder, string filePrefix, int index,
            double targetAngle, double actualAngle, CancellationToken ct)
        {
            string fileName = FormattableString.Invariant($"{filePrefix}_{index:D3}_{targetAngle:000.##}deg.jpg");
            string finalPath = Path.Combine(runFolder, fileName);
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await WaitForRestAsync(actualAngle, ct);
                string attemptFolder = Path.Combine(runFolder, $".attempt_{Guid.NewGuid():N}");
                Directory.CreateDirectory(attemptFolder);
                string attemptPath = Path.Combine(attemptFolder, "frame.jpg");
                var elapsed = Stopwatch.StartNew();
                long size;
                string digest;
                try
                {
                    _camera.TakePicture(attemptPath, FileWaitTimeoutMs, ct);
                    (size, digest) = await CheckFileAsync(attemptPath, ct);
                }
                catch (IOException ex) when (attempt < MaxAttempts)
                {
                    Log?.Invoke(this, $"  ⚠ 第 {index} 张第 {attempt} 次文件失败，同一角度重拍：{ex.Message}");
                    continue;
                }
                catch (IOException ex)
                {
                    throw new IOException($"第 {index} 张在 {MaxAttempts} 次尝试后仍没有合格文件。", ex);
                }
                ct.ThrowIfCancellationRequested();
                File.Move(attemptPath, finalPath);
                return new ShotRecord
                {
                    Index = index, TargetAngle = targetAngle, ActualAngle = actualAngle, FileName = fileName,
                    FileSizeBytes = size, Sha256 = digest, ElapsedMs = elapsed.ElapsedMilliseconds
                };
            }
            throw new InvalidOperationException("未生成拍摄记录。");
        }

        private static async Task<(long Size, string Digest)> CheckFileAsync(string path, CancellationToken ct)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            long size = file.Length;
            if (size < MinFileSizeBytes)
                throw new IOException($"文件只有 {size} B，小于 {MinFileSizeBytes} B。");
            using var hash = SHA256.Create();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await file.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                hash.TransformBlock(buffer, 0, read, buffer, 0);
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return (size, BitConverter.ToString(hash.Hash!).Replace("-", "").ToLowerInvariant());
        }

        private static async Task WriteManifestAsync(string folder, int shotCount,
            IReadOnlyList<ShotRecord> records, CancellationToken ct)
        {
            var text = new StringBuilder();
            text.AppendLine("{");
            text.AppendLine(FormattableString.Invariant($"  \"generated\": \"{DateTime.UtcNow:O}\","));
            text.AppendLine(FormattableString.Invariant($"  \"shotCount\": {shotCount}, \"savedCount\": {records.Count},"));
            text.AppendLine("  \"shots\": [");
            for (int index = 0; index < records.Count; index++)
            {
                var record = records[index];
                string comma = index + 1 < records.Count ? "," : "";
                text.AppendLine(FormattableString.Invariant($"    {{\"index\": {record.Index}, \"targetAngle\": {record.TargetAngle}, \"actualAngle\": {record.ActualAngle}, \"fileName\": \"{record.FileName}\", \"fileSizeBytes\": {record.FileSizeBytes}, \"sha256\": \"{record.Sha256}\", \"elapsedMs\": {record.ElapsedMs}}}{comma}"));
            }
            text.AppendLine("  ]");
            text.AppendLine("}");
            ct.ThrowIfCancellationRequested();
            string temporary = Path.Combine(folder, "manifest.json.tmp");
            byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await file.WriteAsync(bytes, 0, bytes.Length, ct);
                await file.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(folder, "manifest.json"));
        }
    }
}
