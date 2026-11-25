using System.Linq;
using System.Windows;

namespace TurntableStudio
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Any(a => a.ToLowerInvariant() == "--smoketest"))
            {
                int result = Services.SmokeTest.Run(e.Args);
                Shutdown(result);
                return;
            }

            base.OnStartup(e);
            new Views.MainWindow().Show();
        }
    }
}
