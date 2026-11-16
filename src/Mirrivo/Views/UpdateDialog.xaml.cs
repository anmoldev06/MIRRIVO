using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Mirrivo.Services;

namespace Mirrivo.Views;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _updateInfo;
    private CancellationTokenSource? _downloadCts;
    private bool _isDownloading = false;

    public UpdateDialog(UpdateInfo updateInfo)
    {
        InitializeComponent();
        _updateInfo = updateInfo;
        PopulateInfo();
    }

    private void PopulateInfo()
    {
        TxtCurrentVersion.Text = $"v{_updateInfo.CurrentVersion}";
        TxtLatestVersion.Text = string.IsNullOrEmpty(_updateInfo.LatestVersion) ? $"v{_updateInfo.CurrentVersion}" : $"v{_updateInfo.LatestVersion}";

        if (_updateInfo.HasUpdate)
        {
            TxtHeaderTitle.Text = "Update Available";
            TxtReleaseNotes.Text = string.IsNullOrWhiteSpace(_updateInfo.ReleaseNotes)
                ? $"A new version (v{_updateInfo.LatestVersion}) of MIRRIVO is ready to install."
                : _updateInfo.ReleaseNotes;

            if (_updateInfo.AssetSize > 0)
            {
                double sizeMb = _updateInfo.AssetSize / (1024.0 * 1024.0);
                TxtAssetSize.Text = $"Download size: ~{sizeMb:F1} MB";
            }
            else
            {
                TxtAssetSize.Text = "Self-contained portable update";
            }

            BtnAction.Content = "Update Now";
            BtnAction.IsEnabled = !string.IsNullOrWhiteSpace(_updateInfo.DownloadUrl);
        }
        else
        {
            TxtHeaderTitle.Text = "MIRRIVO is Up to Date";
            TxtReleaseNotes.Text = $"You are running the latest version of MIRRIVO (v{_updateInfo.CurrentVersion}). No updates are currently needed.";
            TxtAssetSize.Text = "All features are up to date";
            BtnCancel.Visibility = Visibility.Collapsed;
            BtnAction.Content = "Close";
        }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CancelDownload();
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelDownload();
        Close();
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_updateInfo.HasUpdate)
        {
            Close();
            return;
        }

        if (_isDownloading)
            return;

        _isDownloading = true;
        BtnAction.IsEnabled = false;
        BtnCancel.Content = "Cancel";
        PnlProgress.Visibility = Visibility.Visible;

        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<(double percent, long downloadedBytes, long totalBytes)>(p =>
        {
            PrgDownload.Value = p.percent;
            TxtProgressPercent.Text = $"{p.percent:F0}%";
            double dlMb = p.downloadedBytes / (1024.0 * 1024.0);
            double totalMb = p.totalBytes / (1024.0 * 1024.0);
            TxtProgressStatus.Text = $"Downloading: {dlMb:F1} MB / {totalMb:F1} MB";
        });

        try
        {
            TxtProgressStatus.Text = "Connecting to GitHub...";
            await UpdateService.Instance.DownloadAndApplyUpdateAsync(_updateInfo.DownloadUrl, progress, _downloadCts.Token);
        }
        catch (OperationCanceledException)
        {
            TxtProgressStatus.Text = "Update cancelled.";
            _isDownloading = false;
            BtnAction.IsEnabled = true;
            PnlProgress.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            TxtProgressStatus.Text = "Update failed.";
            MessageBox.Show($"Failed to download or apply update: {ex.Message}", "MIRRIVO OTA Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            _isDownloading = false;
            BtnAction.IsEnabled = true;
            PnlProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelDownload()
    {
        if (_isDownloading && _downloadCts != null)
        {
            _downloadCts.Cancel();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelDownload();
        base.OnClosed(e);
    }
}
