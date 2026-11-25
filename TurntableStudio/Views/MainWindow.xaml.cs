using System.Windows;
using TurntableStudio.ViewModels;

namespace TurntableStudio.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void ManualForwardDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            (DataContext as ViewModels.MainViewModel)?.ManualForwardCommand.Execute(null);
        }

        private void ManualReverseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            (DataContext as ViewModels.MainViewModel)?.ManualReverseCommand.Execute(null);
        }

        private void ManualJogUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            (DataContext as ViewModels.MainViewModel)?.StopRotationCommand.Execute(null);
        }

        protected override void OnClosed(System.EventArgs e)
        {
            (DataContext as MainViewModel)?.Dispose();
            base.OnClosed(e);
        }
    }
}
