using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Mirrivo.Services;

public class AppIconService
{
    private static readonly Lazy<AppIconService> _instance = new(() => new AppIconService());
    public static AppIconService Instance => _instance.Value;

    private readonly string _cacheDirectory;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlightTasks = new(StringComparer.OrdinalIgnoreCase);

    private AppIconService()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mirrivo",
            "IconCache"
        );

        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
        }
        catch { }

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public string? GetCachedIconPath(string packageName)
    {
        try
        {
            string file = Path.Combine(_cacheDirectory, $"{packageName}.png");
            if (File.Exists(file))
            {
                var fi = new FileInfo(file);
                // Valid icon must be between 300 bytes and 2 MB
                if (fi.Length > 300 && fi.Length < 2 * 1024 * 1024)
                {
                    // Check magic bytes for valid PNG (0x89, 0x50, 0x4E, 0x47)
                    using var stream = File.OpenRead(file);
                    byte[] header = new byte[4];
                    int read = stream.Read(header, 0, 4);
                    if (read == 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                    {
                        return file;
                    }
                }

                // If corrupted or non-PNG APK dump, delete it
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
        return null;
    }

    public async Task<string?> GetOrFetchIconAsync(string serial, string packageName, string? apkPath = null)
    {
        string? cached = GetCachedIconPath(packageName);
        if (cached != null) return cached;

        return await _inFlightTasks.GetOrAdd(packageName, async (pkg) =>
        {
            try
            {
                // 1. Try online Play Store official icon fetch FIRST
                // Play Store provides verified, crisp 128px PNG icons with correct branding
                string? webIcon = await TryFetchPlayStoreIconAsync(pkg);
                if (webIcon != null) return webIcon;

                // 2. Fallback to local APK extraction via ADB if device serial is provided
                if (!string.IsNullOrEmpty(serial))
                {
                    string? adbIcon = await TryExtractIconFromDeviceAsync(serial, pkg, apkPath);
                    if (adbIcon != null) return adbIcon;
                }
            }
            catch { }
            finally
            {
                _inFlightTasks.TryRemove(pkg, out _);
            }
            return null;
        });
    }

    private async Task<string?> TryExtractIconFromDeviceAsync(string serial, string packageName, string? apkPath)
    {
        try
        {
            var adb = AdbService.Instance;

            // Resolve APK path if not already given
            string path = apkPath ?? "";
            if (string.IsNullOrWhiteSpace(path))
            {
                var (code, outText, _) = await adb.RunAdbCommandAsync($"-s {serial} shell pm path {packageName}", 3000);
                if (code == 0 && !string.IsNullOrWhiteSpace(outText))
                {
                    foreach (var line in outText.Split('\n'))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("package:"))
                        {
                            path = trimmed.Substring("package:".Length).Trim();
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(path)) return null;

            string targetCacheFile = Path.Combine(_cacheDirectory, $"{packageName}.png");
            string remoteTmpFile = $"/data/local/tmp/mirrivo_icons/{packageName}.png";

            // Extract best resolution PNG using toybox unzip safely
            // Use recursive wildcards (*ic_launcher*.png, *launcher*.png, *icon*.png)
            // AND ensure $icon_file is strictly non-empty before unzipping
            string script = $"rm -f '{remoteTmpFile}' && mkdir -p /data/local/tmp/mirrivo_icons && " +
                            $"icon_file=$(unzip -l '{path}' '*ic_launcher*.png' '*ic_launcher_round*.png' '*launcher*.png' 'res/*icon*.png' 2>/dev/null | grep -E '\\.png$' | sort -k1 -nr | head -n 1 | awk '{{print $4}}') && " +
                            $"if [ -n \"$icon_file\" ]; then unzip -p '{path}' \"$icon_file\" > '{remoteTmpFile}'; fi";

            await adb.RunAdbCommandAsync($"-s {serial} shell \"{script}\"", 5000);

            // Check if remote file exists and is a sane size (between 300 bytes and 2 MB)
            var (statCode, statOut, _) = await adb.RunAdbCommandAsync($"-s {serial} shell \"stat -c %s '{remoteTmpFile}' 2>/dev/null\"", 2000);
            if (long.TryParse(statOut?.Trim(), out long remoteSize) && remoteSize > 300 && remoteSize < 2 * 1024 * 1024)
            {
                var (pullCode, _, _) = await adb.RunAdbCommandAsync($"-s {serial} pull \"{remoteTmpFile}\" \"{targetCacheFile}\"", 4000);
                _ = adb.RunAdbCommandAsync($"-s {serial} shell \"rm -f '{remoteTmpFile}'\"", 2000);

                if (pullCode == 0 && File.Exists(targetCacheFile))
                {
                    var fi = new FileInfo(targetCacheFile);
                    if (fi.Length > 300 && fi.Length < 2 * 1024 * 1024)
                    {
                        using var stream = File.OpenRead(targetCacheFile);
                        byte[] header = new byte[4];
                        int read = stream.Read(header, 0, 4);
                        if (read == 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                        {
                            return targetCacheFile;
                        }
                    }
                    try { File.Delete(targetCacheFile); } catch { }
                }
            }
            else
            {
                // Clean up remote file if size check failed
                _ = adb.RunAdbCommandAsync($"-s {serial} shell \"rm -f '{remoteTmpFile}'\"", 2000);
            }
        }
        catch { }
        return null;
    }

    private async Task<string?> TryFetchPlayStoreIconAsync(string packageName)
    {
        try
        {
            string targetCacheFile = Path.Combine(_cacheDirectory, $"{packageName}.png");
            string url = $"https://play.google.com/store/apps/details?id={packageName}&hl=en";

            string html = await _http.GetStringAsync(url);
            if (string.IsNullOrWhiteSpace(html)) return null;

            // 1. Try og:image meta tag
            var match = Regex.Match(html, @"<meta\s+property=""og:image""\s+content=""([^""]+)""", RegexOptions.IgnoreCase);
            
            // 2. Try modern Play Store Icon image tag
            if (!match.Success)
            {
                match = Regex.Match(html, @"<img[^>]+alt=""Icon image""[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase);
            }

            // 3. Try generic itemprop="image"
            if (!match.Success)
            {
                match = Regex.Match(html, @"itemprop=""image""\s+content=""([^""]+)""", RegexOptions.IgnoreCase);
            }

            if (match.Success)
            {
                string iconUrl = match.Groups[1].Value;
                // Request crisp 128px icon
                if (iconUrl.Contains("googleusercontent.com"))
                {
                    int eqIdx = iconUrl.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        iconUrl = iconUrl.Substring(0, eqIdx) + "=s128";
                    }
                    else
                    {
                        iconUrl += "=s128";
                    }
                }

                byte[] imageBytes = await _http.GetByteArrayAsync(iconUrl);
                if (imageBytes.Length > 200 && imageBytes.Length < 2 * 1024 * 1024)
                {
                    await File.WriteAllBytesAsync(targetCacheFile, imageBytes);
                    return targetCacheFile;
                }
            }
        }
        catch { }
        return null;
    }

    public async Task<string?> FetchPlayStoreTitleAsync(string packageName)
    {
        try
        {
            string url = $"https://play.google.com/store/apps/details?id={packageName}&hl=en";
            string html = await _http.GetStringAsync(url);
            if (string.IsNullOrWhiteSpace(html)) return null;

            var match = Regex.Match(html, @"<meta\s+property=""og:title""\s+content=""([^""]+)""", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string title = match.Groups[1].Value.Trim();
                int idx = title.IndexOf(" - Apps on Google Play", StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    title = title.Substring(0, idx).Trim();
                }
                return title;
            }
        }
        catch { }
        return null;
    }
}
