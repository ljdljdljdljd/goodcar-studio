using System;
using System.IO;
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

    /// <summary>
    /// 步进精拍编排器：转盘 MoveTo → 停稳确认 → 拍照 → 文件落盘校验 → 下一角度
    /// 每角度最多重试 3 次，完成后生成 manifest.json
    /// </summary>
    public sealed class CaptureOrchestrator
    {
        private readonly TurntableService _turntable;
        private readonly CameraService _camera;

        private const int MaxRetries = 3;
        private const long MinFileSizeBytes = 20480; // 20 KB
        private const int FileWaitIntervalMs = 200;
        private const int FileWaitTimeoutMs = 30000; // 30s 等待相机传图

        public event EventHandler<CaptureProgressEventArgs>? Progress;
        public event EventHandler<string>? Log;

        public CaptureOrchestrator(TurntableService turntable, CameraService camera)
        {
            _turntable = turntable;
            _camera = camera;
        }

        /// <summary>
        /// 执行完整的一圈精拍任务
        /// </summary>
        /// <param name="shotCount">张数（36 或 72）</param>
        /// <param name="saveFolder">保存目录</param>
        /// <param name="filePrefix">文件名前缀（默认 Shot）</param>
        /// <param name="ct">取消令牌</param>
        public async Task RunAsync(int shotCount, string saveFolder,
            string filePrefix = "Shot", CancellationToken ct = default)
        {
            if (shotCount <= 0 || shotCount > 360)
                throw new ArgumentOutOfRangeException(nameof(shotCount));

            Directory.CreateDirectory(saveFolder);
            double stepAngle = 360.0 / shotCount;
            var records = new System.Collections.Generic.List<ShotRecord>(shotCount);

            for (int i = 0; i < shotCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                double targetAngle = Math.Round(i * stepAngle, 4);
                string fileName = $"{filePrefix}_{i + 1:D3}_{targetAngle:000.##}deg.jpg";
                string filePath = Path.Combine(saveFolder, fileName);

                bool success = false;
                for (int attempt = 1; attempt <= MaxRetries && !success; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    Log?.Invoke(this, $"[{i + 1}/{shotCount}] 转动到 {targetAngle:F1}°（第{attempt}次）");

                    try
                    {
                        // 1. 转盘移动并等待停稳
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        await _turntable.MoveToAsync(targetAngle, toleranceDeg: 0.5,
                            stabilizeMs: 300, timeoutMs: 10000);
                        double actualAngle = _turntable.ReadAngle();

                        // 2. 配置保存路径并触发快门
                        // 删除可能存在的旧文件（重试场景）
                        if (File.Exists(filePath)) File.Delete(filePath);
                        _camera.TakePicture(holdMs: 100);

                        // 3. 轮询等待文件落盘（相机异步传图）
                        bool fileReady = await WaitForFileAsync(filePath, ct);
                        if (!fileReady)
                        {
                            Log?.Invoke(this, $"  ⚠ 文件未在 {FileWaitTimeoutMs}ms 内落盘，重试...");
                            continue;
                        }

                        // 4. 文件完整性校验
                        var fi = new FileInfo(filePath);
                        if (fi.Length < MinFileSizeBytes)
                        {
                            Log?.Invoke(this, $"  ⚠ 文件过小 ({fi.Length} B < {MinFileSizeBytes} B)，重试...");
                            continue;
                        }

                        sw.Stop();
                        string sha256 = ComputeSha256(filePath);
                        records.Add(new ShotRecord
                        {
                            Index = i + 1,
                            TargetAngle = targetAngle,
                            ActualAngle = Math.Round(actualAngle, 3),
                            FileName = fileName,
                            FileSizeBytes = fi.Length,
                            Sha256 = sha256,
                            ElapsedMs = sw.ElapsedMilliseconds,
                        });

                        success = true;
                        Log?.Invoke(this, $"  ✓ {fileName} ({fi.Length / 1024}KB, {sw.ElapsedMilliseconds}ms)");
                        Progress?.Invoke(this, new CaptureProgressEventArgs
                        {
                            Current = i + 1,
                            Total = shotCount,
                            FileName = fileName,
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log?.Invoke(this, $"  ✗ 第{attempt}次失败：{ex.Message}");
                        if (attempt == MaxRetries)
                            throw new InvalidOperationException(
                                $"角度 {targetAngle:F1}° 拍摄失败（已重试 {MaxRetries} 次）", ex);
                    }
                }
            }

            // 5. 生成 manifest.json
            await WriteManifestAsync(saveFolder, shotCount, records);
            Log?.Invoke(this, $"✅ 拍摄完成，共 {records.Count} 张，manifest.json 已生成");
        }

        private static async Task<bool> WaitForFileAsync(string filePath, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(FileWaitTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(filePath))
                {
                    // 额外等待 200ms 确认写入完成
                    await Task.Delay(200, ct);
                    return true;
                }
                await Task.Delay(FileWaitIntervalMs, ct);
            }
            return false;
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(filePath);
            byte[] hash = sha.ComputeHash(fs);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static async Task WriteManifestAsync(string saveFolder, int shotCount,
            System.Collections.Generic.List<ShotRecord> records)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"generated\": \"{DateTime.UtcNow:O}\",");
            sb.AppendLine($"  \"shotCount\": {shotCount},");
            sb.AppendLine($"  \"savedCount\": {records.Count},");
            sb.AppendLine("  \"shots\": [");
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                string comma = i < records.Count - 1 ? "," : "";
                sb.AppendLine($"    {{ \"index\": {r.Index}, \"targetAngle\": {r.TargetAngle}, \"actualAngle\": {r.ActualAngle}, \"fileName\": \"{r.FileName}\", \"fileSizeBytes\": {r.FileSizeBytes}, \"sha256\": \"{r.Sha256}\", \"elapsedMs\": {r.ElapsedMs} }}{comma}");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            string path = Path.Combine(saveFolder, "manifest.json");
            await Task.Run(() => File.WriteAllText(path, sb.ToString(), Encoding.UTF8));
        }
    }
}
