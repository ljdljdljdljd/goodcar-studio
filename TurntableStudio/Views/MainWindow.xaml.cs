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

        protected override void OnClosed(System.EventArgs e)
        {
            (DataContext as MainViewModel)?.Dispose();
            base.OnClosed(e);
        }
    }
}
