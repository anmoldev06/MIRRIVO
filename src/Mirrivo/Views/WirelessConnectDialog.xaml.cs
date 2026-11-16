using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Mirrivo.Services;

namespace Mirrivo.Views;

public partial class WirelessConnectDialog : Window
{
    private bool _isPairingMode = false;

    public WirelessConnectDialog()
    {
        InitializeComponent();
        MouseDown += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
    }

    private void TabDirect_Click(object sender, RoutedEventArgs e)
    {
        _isPairingMode = false;
        TabDirectBtn.Background = (Brush)FindResource("BrushCanvasSoft");
        TabDirectBtn.BorderThickness = new Thickness(1);
        TabPairBtn.Background = Brushes.Transparent;
        TabPairBtn.BorderThickness = new Thickness(0);

        PairCodePanel.Visibility = Visibility.Collapsed;
        TxtPort.Text = "5555";
        BtnConnect.Content = "Connect Device";
        TxtHelpTip.Text = "Phone me Developer Options > Wireless Debugging ON karein. Dono PC aur Phone same Wi-Fi pe hone chahiye.";
    }

    private void TabPair_Click(object sender, RoutedEventArgs e)
    {
        _isPairingMode = true;
        TabPairBtn.Background = (Brush)FindResource("BrushCanvasSoft");
        TabPairBtn.BorderThickness = new Thickness(1);
        TabDirectBtn.Background = Brushes.Transparent;
        TabDirectBtn.BorderThickness = new Thickness(0);

        PairCodePanel.Visibility = Visibility.Visible;
        TxtPort.Text = "";
        BtnConnect.Content = "Pair & Connect";
        TxtHelpTip.Text = "Phone me Wireless Debugging > 'Pair device with pairing code' par tap karein aur wahan dikh raha IP:Port aur 6-digit Code yahan enter karein.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        string ip = TxtIpAddress.Text.Trim();
        if (string.IsNullOrWhiteSpace(ip) || !int.TryParse(TxtPort.Text.Trim(), out int port))
        {
            MessageBox.Show("Please enter a valid IP address and Port.", "MIRRIVO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnConnect.IsEnabled = false;
        BtnConnect.Content = "Connecting...";

        if (_isPairingMode)
        {
            string code = TxtPairCode.Text.Trim();
            if (string.IsNullOrWhiteSpace(code) || code.Length < 6)
            {
                MessageBox.Show("Please enter the 6-digit pairing code.", "MIRRIVO", MessageBoxButton.OK, MessageBoxImage.Warning);
                BtnConnect.IsEnabled = true;
                BtnConnect.Content = "Pair & Connect";
                return;
            }

            var (pairSuccess, pairMsg) = await AdbService.Instance.PairWirelessAsync(ip, port, code);
            if (!pairSuccess)
            {
                MessageBox.Show($"Pairing failed: {pairMsg}", "MIRRIVO", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnConnect.IsEnabled = true;
                BtnConnect.Content = "Pair & Connect";
                return;
            }

            // Once paired, connect
            await AdbService.Instance.ConnectWirelessAsync(ip, 5555);
        }
        else
        {
            var (connSuccess, connMsg) = await AdbService.Instance.ConnectWirelessAsync(ip, port);
            if (!connSuccess)
            {
                MessageBox.Show($"Connection failed: {connMsg}", "MIRRIVO", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnConnect.IsEnabled = true;
                BtnConnect.Content = "Connect Device";
                return;
            }
        }

        DialogResult = true;
        Close();
    }
}
