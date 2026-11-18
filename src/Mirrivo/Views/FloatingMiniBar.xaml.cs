using System.Windows;
using System.Windows.Input;

namespace Mirrivo.Views;

public partial class FloatingMiniBar : Window
{
    public FloatingMiniBar()
    {
        InitializeComponent();

        // Position at top center/right of primary screen
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = screenWidth - Width - 80;
        Top = 60;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
