using System;
using System.Windows;

namespace TurntableStudio
{
    public partial class App : Application
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("--smoketest", StringComparison.OrdinalIgnoreCase))
            {
                // 命令行自检模式，不启动 WPF 窗口
                return SmokeTest.Run(args);
            }

            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            new Views.MainWindow().Show();
        }
    }
}
