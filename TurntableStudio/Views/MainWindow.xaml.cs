using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using TurntableStudio.ViewModels;

namespace TurntableStudio.Views
{
    public partial class MainWindow : Window
    {
        private UIElement? _jogOwner;
        private bool _shutdownPending;
        private bool _shutdownComplete;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void ManualForwardDown(object sender, MouseButtonEventArgs e)
        {
            BeginJog(sender, e, true);
        }

        private void ManualReverseDown(object sender, MouseButtonEventArgs e)
        {
            BeginJog(sender, e, false);
        }

        private void BeginJog(object sender, MouseButtonEventArgs e, bool forward)
        {
            e.Handled = true;
            if (_jogOwner != null || sender is not UIElement element ||
                DataContext is not MainViewModel model || !model.CanUseManual) return;
            var command = forward ? model.ManualForwardCommand : model.ManualReverseCommand;
            if (!command.CanExecute(null) || !element.CaptureMouse()) return;
            _jogOwner = element;
            command.Execute(null);
        }

        private void ManualJogUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            EndJog(sender);
        }

        private void ManualJogLostCapture(object sender, MouseEventArgs e) => EndJog(sender);

        private void EndJog(object sender)
        {
            if (_jogOwner == null || !ReferenceEquals(sender, _jogOwner)) return;
            var owner = _jogOwner;
            _jogOwner = null;
            if (owner.IsMouseCaptured) owner.ReleaseMouseCapture();
            var stop = (DataContext as MainViewModel)?.StopRotationCommand;
            if (stop?.CanExecute(null) == true) stop.Execute(null);
        }

        protected override async void OnClosing(CancelEventArgs e)
        {
            if (_shutdownComplete || DataContext is not MainViewModel model)
            {
                base.OnClosing(e);
                return;
            }
            base.OnClosing(e);
            e.Cancel = true;
            if (_shutdownPending) return;
            _shutdownPending = true;
            try
            {
                await model.ShutdownAsync();
                _shutdownComplete = true;
                Dispatcher.BeginInvoke(new Action(Close));
            }
            catch (Exception ex)
            {
                _shutdownPending = false;
                MessageBox.Show(this, ex.Message, "设备关闭失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
