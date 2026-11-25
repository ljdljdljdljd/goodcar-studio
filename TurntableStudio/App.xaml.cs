using System.Windows;

namespace TurntableStudio
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            new Views.MainWindow().Show();
        }
    }
}
