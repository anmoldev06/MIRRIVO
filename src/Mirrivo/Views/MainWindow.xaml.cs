using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mirrivo.ViewModels;

namespace Mirrivo.Views;

public partial class MainWindow : Window
{
    private readonly Brush _defaultDropBackground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
    private readonly Brush _hoverDropBackground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0F6FF"));
    private readonly Brush _defaultDropBorder = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E5E5EA"));
    private readonly Brush _hoverDropBorder = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0071E3"));

    public MainWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            AnimateDropZone(isHovering: true);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        AnimateDropZone(isHovering: false);
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        AnimateDropZone(isHovering: false);

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[]? files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && DataContext is MainViewModel vm)
            {
                await vm.HandleDropFilesAsync(files);
            }
        }
        e.Handled = true;
    }

    private void AnimateDropZone(bool isHovering)
    {
        if (DropZoneBorder == null || DropZoneText == null || DropZoneIcon == null) return;

        if (isHovering)
        {
            DropZoneBorder.Background = _hoverDropBackground;
            DropZoneBorder.BorderBrush = _hoverDropBorder;
            DropZoneBorder.BorderThickness = new Thickness(1.5);
            DropZoneText.Text = "Release to transfer file to phone 🚀";
            DropZoneText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0070F3"));
            DropZoneIcon.Text = "✨";
        }
        else
        {
            DropZoneBorder.Background = _defaultDropBackground;
            DropZoneBorder.BorderBrush = _defaultDropBorder;
            DropZoneBorder.BorderThickness = new Thickness(1);
            DropZoneText.Text = "Drag & drop files anywhere to share instantly with phone";
            DropZoneText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D1D1F"));
            DropZoneIcon.Text = "📥";
        }
    }
}
