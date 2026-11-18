using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mirrivo.Services;
using Mirrivo.ViewModels;

namespace Mirrivo.Views;

public partial class FloatingMiniBar : Window
{
    private readonly DispatcherTimer _dockTimer;
    private bool _isDocked = true;

    public FloatingMiniBar()
    {
        InitializeComponent();

        _dockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(25) // ~40 FPS magnetic tracking
        };
        _dockTimer.Tick += DockTimer_Tick;
        _dockTimer.Start();

        Closed += (s, e) => _dockTimer.Stop();
    }

    private void DockTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isDocked) return;

        try
        {
            if (DataContext is not MainViewModel vm) return;

            string serial = vm.SelectedDevice?.Serial ?? "";
            IntPtr hWnd = MirrorService.Instance.GetMirrorWindowHandle(serial);

            if (hWnd != IntPtr.Zero)
            {
                // If scrcpy window is minimized, hide companion bar
                if (MirrorService.IsIconic(hWnd))
                {
                    if (Visibility != Visibility.Collapsed)
                    {
                        Visibility = Visibility.Collapsed;
                    }
                    return;
                }

                if (MirrorService.GetWindowRect(hWnd, out var rect))
                {
                    if (rect.Width > 50 && rect.Height > 50)
                    {
                        var source = PresentationSource.FromVisual(this);
                        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                        double barHeight = ActualHeight > 0 ? ActualHeight : 440;
                        double barWidth = ActualWidth > 0 ? ActualWidth : 46;

                        // Stick directly to the right edge of phone window
                        double targetLeft = (rect.Right / dpiX) + 3;
                        double targetTop = (rect.Top / dpiY) + Math.Max(20, ((rect.Height / dpiY) - barHeight) / 2);

                        // If phone is near right edge of screen, dock on left side instead
                        if (targetLeft + barWidth > SystemParameters.VirtualScreenWidth)
                        {
                            targetLeft = (rect.Left / dpiX) - barWidth - 3;
                        }

                        // Prevent vertical overflow
                        if (targetTop < 10) targetTop = 10;
                        if (targetTop + barHeight > SystemParameters.VirtualScreenHeight)
                        {
                            targetTop = SystemParameters.VirtualScreenHeight - barHeight - 10;
                        }

                        Left = targetLeft;
                        Top = targetTop;

                        if (Visibility != Visibility.Visible)
                        {
                            Visibility = Visibility.Visible;
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore transient Win32 coordinate calculation errors
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _isDocked = !_isDocked;
        UpdatePinAppearance();
    }

    private void UpdatePinAppearance()
    {
        if (_isDocked)
        {
            PathPin.Fill = new SolidColorBrush(Color.FromRgb(0, 112, 243));
            PathPin.Stroke = new SolidColorBrush(Color.FromRgb(0, 112, 243));
            BtnPin.ToolTip = "Attached to Phone (Click to undock)";
        }
        else
        {
            PathPin.Fill = new SolidColorBrush(Color.FromRgb(140, 140, 140));
            PathPin.Stroke = new SolidColorBrush(Color.FromRgb(140, 140, 140));
            BtnPin.ToolTip = "Undocked / Free Floating (Click to snap back to Phone)";
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isDocked = false;
            UpdatePinAppearance();
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _dockTimer.Stop();
        Close();
    }
}
