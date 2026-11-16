using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Mirrivo.Services;

public class UpdateInfo
{
    public bool HasUpdate { get; set; }
    public string CurrentVersion { get; set; } = "1.0.1";
    public string LatestVersion { get; set; } = string.Empty;
    public string ReleaseTitle { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long AssetSize { get; set; }
    public string HtmlUrl { get; set; } = string.Empty;
}

public class UpdateService
{
    private static readonly Lazy<UpdateService> _instance = new(() => new UpdateService());
    public static UpdateService Instance => _instance.Value;

    public const string CurrentVersionString = "1.0.1";
    public static readonly Version CurrentVersion = new(1, 0, 1);
    public const string GitHubRepo = "anmoldev06/MIRRIVO";

    private readonly HttpClient _httpClient;

    public UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mirrivo-App", CurrentVersionString));
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Checks GitHub Releases API for the latest release.
    /// </summary>
    public async Task<UpdateInfo> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var info = new UpdateInfo
        {
            CurrentVersion = CurrentVersionString,
            HasUpdate = false
        };

        try
        {
            string url = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return info;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            string name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() ?? "" : "";

            info.LatestVersion = tagName.TrimStart('v', 'V');
            info.ReleaseTitle = string.IsNullOrWhiteSpace(name) ? tagName : name;
            info.ReleaseNotes = body;
            info.HtmlUrl = htmlUrl;

            // Search for downloadable .exe asset
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string assetName = asset.TryGetProperty("name", out var aName) ? aName.GetString() ?? "" : "";
                    if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        info.DownloadUrl = asset.TryGetProperty("browser_download_url", out var dlProp) ? dlProp.GetString() ?? "" : "";
                        info.AssetSize = asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
                        break;
                    }
                }
            }

            // Compare version
            if (Version.TryParse(info.LatestVersion, out var latestVer))
            {
                info.HasUpdate = latestVer > CurrentVersion;
            }
            else if (!string.IsNullOrEmpty(info.LatestVersion) && !string.Equals(info.LatestVersion, CurrentVersionString, StringComparison.OrdinalIgnoreCase))
            {
                info.HasUpdate = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] Error checking for update: {ex.Message}");
        }

        return info;
    }

    /// <summary>
    /// Downloads the update file and launches an updater script to replace the current executable.
    /// </summary>
    public async Task DownloadAndApplyUpdateAsync(
        string downloadUrl,
        IProgress<(double percent, long downloadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new ArgumentException("Download URL cannot be empty.", nameof(downloadUrl));

        string currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mirrivo.exe");

        string updateDir = Path.Combine(Path.GetTempPath(), "Mirrivo_Update");
        Directory.CreateDirectory(updateDir);
        string tempExe = Path.Combine(updateDir, "Mirrivo_New.exe");
        string updaterBat = Path.Combine(updateDir, "updater.bat");

        // 1. Download file with progress reporting
        using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    double percent = (double)totalRead / totalBytes * 100.0;
                    progress?.Report((percent, totalRead, totalBytes));
                }
            }
        }

        // 2. Generate self-replacing updater batch script
        int currentPid = Process.GetCurrentProcess().Id;
        string batContent = $@"@echo off
chcp 65001 > nul
title MIRRIVO OTA Updater
echo Updating MIRRIVO to the latest version...
timeout /t 1 /nobreak > nul

set RETRIES=0
:loop
taskkill /PID {currentPid} /F > nul 2>&1
timeout /t 1 /nobreak > nul

move /y ""{tempExe}"" ""{currentExe}"" > nul 2>&1
if %errorlevel% equ 0 goto success

set /a RETRIES+=1
if %RETRIES% geq 30 goto error
goto loop

:success
echo Launching updated MIRRIVO...
start """" ""{currentExe}""
del ""%~f0""
exit

:error
echo Failed to replace binary. You can find the downloaded update at:
echo ""{tempExe}""
pause
exit
";
        await File.WriteAllTextAsync(updaterBat, batContent, ct);

        // 3. Launch updater.bat detached and shutdown current app
        var psi = new ProcessStartInfo
        {
            FileName = updaterBat,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = updateDir
        };

        Process.Start(psi);

        // 4. Shutdown WPF app
        Application.Current.Dispatcher.Invoke(() =>
        {
            Application.Current.Shutdown();
        });
    }
}
