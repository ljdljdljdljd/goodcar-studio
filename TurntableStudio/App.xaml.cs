using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace TurntableStudio
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 全局未捕获异常 → 弹窗显示，不静默崩溃
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                MessageBox.Show(
                    ex.ExceptionObject?.ToString() ?? "未知错误",
                    "TurntableStudio 启动错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            };
            DispatcherUnhandledException += (s, ex) =>
            {
                MessageBox.Show(
                    ex.Exception?.ToString() ?? "未知错误",
                    "TurntableStudio UI 错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            if (e.Args.Any(a => a.ToLowerInvariant() == "--smoketest"))
            {
                int result = Services.SmokeTest.Run(e.Args);
                Shutdown(result);
                return;
            }

            base.OnStartup(e);
            try
            {
                new Views.MainWindow().Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.ToString(),
                    "主窗口初始化失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }
    }
}
