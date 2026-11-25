using System;
using System.IO;
using System.Text;
using TurntableStudio.Services;

namespace TurntableStudio
{
    /// <summary>
    /// 命令行自检工具：验证 SonyCrBridge.dll 和转盘 SDK 的基础链路是否正常。
    /// 用法：TurntableStudio.exe --smoketest [plcIp]
    /// </summary>
    public static class SmokeTest
    {
        public static int Run(string[] args)
        {
            string plcIp = args.Length > 1 ? args[1] : "192.168.1.111";
            var log = new StringBuilder();
            int passed = 0, failed = 0;

            void Pass(string msg) { Console.WriteLine($"  [PASS] {msg}"); log.AppendLine($"PASS: {msg}"); passed++; }
            void Fail(string msg) { Console.WriteLine($"  [FAIL] {msg}"); log.AppendLine($"FAIL: {msg}"); failed++; }
            void Section(string title) { Console.WriteLine($"\n── {title} ──"); }

            Console.WriteLine("GoodCar 影棚拍摄系统 SmokeTest");
            Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"PLC IP：{plcIp}");

            // ── 1. SonyCrBridge.dll 加载 ──────────────────────────────
            Section("1. SonyCrBridge.dll 加载与 SDK 初始化");
            CameraService? camera = null;
            try
            {
                camera = new CameraService();
                camera.Initialize();
                Pass("CrBridge_Initialize 成功");
            }
            catch (Exception ex)
            {
                Fail($"CrBridge_Initialize 失败：{ex.Message}");
                goto Report;
            }

            // ── 2. 枚举相机 ───────────────────────────────────────────
            Section("2. 枚举已连接相机");
            int deviceCount = 0;
            try
            {
                deviceCount = camera.GetDeviceCount();
                if (deviceCount > 0)
                    Pass($"发现 {deviceCount} 台相机");
                else
                    Fail("未发现任何相机，请检查 USB 连接与 PC 遥控设置（菜单 → 传输 & 遥控 → PC 遥控功能 → 开启）");
            }
            catch (Exception ex)
            {
                Fail($"GetDeviceCount 失败：{ex.Message}");
            }

            if (deviceCount > 0)
            {
                Section("3. 相机设备描述");
                try
                {
                    string desc = camera.GetDeviceDescriptor(0);
                    Pass($"描述符：{desc}");
                }
                catch (Exception ex) { Fail($"GetDeviceDescriptor 失败：{ex.Message}"); }

                Section("4. 连接相机");
                try
                {
                    camera.Connect(0, timeoutMs: 15000);
                    Pass("CrBridge_Connect 成功");

                    Section("5. 实时取景（取一帧）");
                    try
                    {
                        camera.StartLiveView();
                        System.Threading.Thread.Sleep(500);
                        byte[]? frame = camera.GetLiveViewJpeg(2000);
                        if (frame != null && frame.Length > 1024)
                            Pass($"LiveView 帧大小：{frame.Length / 1024} KB");
                        else
                            Fail($"LiveView 帧无效（大小：{frame?.Length ?? 0} B）");
                        camera.StopLiveView();
                    }
                    catch (Exception ex) { Fail($"LiveView 失败：{ex.Message}"); }

                    Section("6. 快门测试（1 次拍照）");
                    try
                    {
                        camera.TakePicture(holdMs: 100);
                        Pass("TakePicture 返回成功");
                    }
                    catch (Exception ex) { Fail($"TakePicture 失败：{ex.Message}"); }
                }
                catch (Exception ex) { Fail($"Connect 失败：{ex.Message}"); }
                finally { try { camera.Disconnect(); } catch { } }
            }

            // ── 7. 转盘 PLC 连接 ──────────────────────────────────────
            Section($"7. 转盘 PLC 连接（{plcIp}）");
            using var turntable = new TurntableService();
            try
            {
                turntable.Connect(plcIp);
                System.Threading.Thread.Sleep(1000);
                if (turntable.IsConnected)
                {
                    Pass($"PLC 已连接：{plcIp}");
                    Section("8. 读取当前角度");
                    try
                    {
                        double angle = turntable.ReadAngle();
                        Pass($"当前角度：{angle:F2}°");
                    }
                    catch (Exception ex) { Fail($"ReadAngle 失败：{ex.Message}"); }
                }
                else
                {
                    Fail($"PLC 连接状态未就绪：{plcIp}");
                }
            }
            catch (Exception ex) { Fail($"TurntableService.Connect 失败：{ex.Message}"); }

        Report:
            camera?.Dispose();
            Console.WriteLine($"\n══════════════════════════════");
            Console.WriteLine($"SmokeTest 结果：{passed} PASS / {failed} FAIL");
            Console.WriteLine($"══════════════════════════════");

            string reportPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                $"smoketest_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(reportPath, log.ToString());
            Console.WriteLine($"报告已保存：{reportPath}");

            return failed == 0 ? 0 : 1;
        }
    }
}
