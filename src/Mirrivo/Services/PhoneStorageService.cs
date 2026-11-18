using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;
using NWebDav.Server;
using NWebDav.Server.Http;
using NWebDav.Server.HttpListener;
using NWebDav.Server.Locking;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;

namespace Mirrivo.Services;

public record AdbEntryInfo(string FullPath, string Name, bool IsDirectory, long Size, DateTime Modified);

public class AdbStore : IStore
{
    private readonly string _serial;
    private readonly string _adbPath;
    private readonly string _cacheDir;
    private readonly ILockingManager _lockingManager = new InMemoryLockingManager();

    public AdbStore(string serial, string adbPath)
    {
        _serial = serial;
        _adbPath = adbPath;
        _cacheDir = Path.Combine(Path.GetTempPath(), "MirrivoCache", serial.Replace(':', '_').Replace('.', '_'));
        if (!Directory.Exists(_cacheDir)) Directory.CreateDirectory(_cacheDir);
    }

    public Task<IStoreItem?> GetItemAsync(Uri uri, IHttpContext httpContext)
    {
        string path = CleanPath(uri);
        if (path == "/sdcard" || path == "/sdcard/")
        {
            return Task.FromResult<IStoreItem?>(new AdbCollection(this, "/sdcard", "sdcard"));
        }

        var info = GetPathInfo(path);
        if (info == null) return Task.FromResult<IStoreItem?>(null);

        if (info.IsDirectory)
            return Task.FromResult<IStoreItem?>(new AdbCollection(this, info.FullPath, info.Name, info.Modified));
        else
            return Task.FromResult<IStoreItem?>(new AdbFile(this, info.FullPath, info.Name, info.Size, info.Modified));
    }

    public Task<IStoreCollection?> GetCollectionAsync(Uri uri, IHttpContext httpContext)
    {
        string path = CleanPath(uri);
        if (path == "/sdcard" || path == "/sdcard/")
        {
            return Task.FromResult<IStoreCollection?>(new AdbCollection(this, "/sdcard", "sdcard"));
        }

        var info = GetPathInfo(path);
        if (info != null && info.IsDirectory)
        {
            return Task.FromResult<IStoreCollection?>(new AdbCollection(this, info.FullPath, info.Name, info.Modified));
        }

        return Task.FromResult<IStoreCollection?>(null);
    }

    private string CleanPath(Uri uri)
    {
        string rel = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        if (rel.StartsWith("DavWWWRoot", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel.Substring("DavWWWRoot".Length).TrimStart('/');
        }
        if (string.IsNullOrEmpty(rel)) return "/sdcard";
        return "/sdcard/" + rel;
    }

    private void EnsureConnected()
    {
        if (_serial.Contains(':'))
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = _adbPath,
                    Arguments = $"connect {_serial}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                p?.WaitForExit(4000);
            }
            catch { }
        }
    }

    private string ExecuteAdbProcess(string args, int timeoutMs, out string err)
    {
        err = string.Empty;
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _adbPath,
                    Arguments = $"-s {_serial} {args}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            p.Start();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (p.WaitForExit(timeoutMs))
            {
                Task.WaitAll(outTask, errTask);
                err = errTask.Result;
                return outTask.Result;
            }
            else
            {
                try { p.Kill(); } catch { }
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("AdbStore ADB error: " + ex.Message);
            err = ex.Message;
            return string.Empty;
        }
    }

    public string RunAdb(string args, int timeoutMs = 15000)
    {
        string outStr = ExecuteAdbProcess(args, timeoutMs, out string err);
        if (err.Contains("offline") || err.Contains("not found") || outStr.Contains("offline"))
        {
            EnsureConnected();
            outStr = ExecuteAdbProcess(args, timeoutMs, out _);
        }
        return outStr;
    }

    private bool ExecuteAdbTransfer(string args, int timeoutMs, out string err)
    {
        err = string.Empty;
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _adbPath,
                    Arguments = $"-s {_serial} {args}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            p.Start();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (p.WaitForExit(timeoutMs))
            {
                Task.WaitAll(outTask, errTask);
                err = errTask.Result;
                return p.ExitCode == 0;
            }
            else
            {
                try { p.Kill(); } catch { }
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("AdbStore transfer error: " + ex.Message);
            err = ex.Message;
            return false;
        }
    }

    public bool RunAdbTransfer(string args, int timeoutMs = 180000)
    {
        bool ok = ExecuteAdbTransfer(args, timeoutMs, out string err);
        if (!ok && (err.Contains("offline") || err.Contains("not found")))
        {
            EnsureConnected();
            ok = ExecuteAdbTransfer(args, timeoutMs, out _);
        }
        return ok;
    }

    public AdbEntryInfo? GetPathInfo(string fullPath)
    {
        string output = RunAdb($"shell \"stat -c '%F|%s|%Y|%n' '{fullPath}' 2>/dev/null\"");
        var line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(line)) return null;

        var parts = line.Split('|');
        if (parts.Length < 4) return null;

        bool isDir = parts[0].Contains("directory");
        long.TryParse(parts[1], out long size);
        long.TryParse(parts[2], out long sec);
        DateTime modified = DateTimeOffset.FromUnixTimeSeconds(sec > 0 ? sec : DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
        string name = Path.GetFileName(fullPath.TrimEnd('/'));
        if (string.IsNullOrEmpty(name)) name = "sdcard";

        return new AdbEntryInfo(fullPath, name, isDir, size, modified);
    }

    public List<AdbEntryInfo> ListEntries(string directoryPath)
    {
        var list = new List<AdbEntryInfo>();
        string cleanDir = directoryPath.TrimEnd('/');
        string target = $"\"{cleanDir}/\"*";
        string output = RunAdb($"shell \"stat -c '%F|%s|%Y|%n' {target} 2>/dev/null\"");
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var l in lines)
        {
            var parts = l.Split('|');
            if (parts.Length < 4) continue;
            bool isDir = parts[0].Contains("directory");
            long.TryParse(parts[1], out long size);
            long.TryParse(parts[2], out long sec);
            DateTime modified = DateTimeOffset.FromUnixTimeSeconds(sec > 0 ? sec : DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
            string full = parts[3].Trim();
            string name = Path.GetFileName(full);
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new AdbEntryInfo(full, name, isDir, size, modified));
        }

        return list;
    }

    public ILockingManager LockingManager => _lockingManager;
    public string CacheDir => _cacheDir;
}

public class AdbCollection : IStoreCollection, IStoreItem
{
    private readonly AdbStore _store;
    private readonly string _fullPath;
    private readonly string _name;
    private readonly DateTime _modified;

    public AdbCollection(AdbStore store, string fullPath, string name, DateTime? modified = null)
    {
        _store = store;
        _fullPath = fullPath;
        _name = name;
        _modified = modified ?? DateTime.UtcNow;

        PropertyManager = new PropertyManager<AdbCollection>(new DavProperty<AdbCollection>[]
        {
            new DavCreationDate<AdbCollection> { Getter = (c, e) => e._modified },
            new DavDisplayName<AdbCollection> { Getter = (c, e) => e._name },
            new DavGetLastModified<AdbCollection> { Getter = (c, e) => e._modified },
            new DavGetResourceType<AdbCollection> { Getter = (c, e) => new[] { new XElement(WebDavNamespaces.DavNs + "collection") } },
            new DavLockDiscoveryDefault<AdbCollection>(),
            new DavSupportedLockDefault<AdbCollection>()
        });
    }

    public string Name => _name;
    public string UniqueKey => _fullPath;
    public IPropertyManager PropertyManager { get; }
    public ILockingManager LockingManager => _store.LockingManager;
    public InfiniteDepthMode InfiniteDepthMode => InfiniteDepthMode.Allowed;

    public Task<IStoreItem?> GetItemAsync(string name, IHttpContext httpContext)
    {
        string childPath = _fullPath.TrimEnd('/') + "/" + name;
        var info = _store.GetPathInfo(childPath);
        if (info == null) return Task.FromResult<IStoreItem?>(null);

        if (info.IsDirectory)
            return Task.FromResult<IStoreItem?>(new AdbCollection(_store, info.FullPath, info.Name, info.Modified));
        else
            return Task.FromResult<IStoreItem?>(new AdbFile(_store, info.FullPath, info.Name, info.Size, info.Modified));
    }

    public Task<IEnumerable<IStoreItem>> GetItemsAsync(IHttpContext httpContext)
    {
        var entries = _store.ListEntries(_fullPath);
        var items = new List<IStoreItem>();
        foreach (var e in entries)
        {
            if (e.IsDirectory)
                items.Add(new AdbCollection(_store, e.FullPath, e.Name, e.Modified));
            else
                items.Add(new AdbFile(_store, e.FullPath, e.Name, e.Size, e.Modified));
        }
        return Task.FromResult<IEnumerable<IStoreItem>>(items);
    }

    public Task<StoreItemResult> CreateItemAsync(string name, bool overwrite, IHttpContext httpContext)
    {
        string childPath = _fullPath.TrimEnd('/') + "/" + name;
        _store.RunAdb($"shell \"touch '{childPath}'\"");
        var file = new AdbFile(_store, childPath, name, 0, DateTime.UtcNow);
        return Task.FromResult(new StoreItemResult(DavStatusCode.Created, file));
    }

    public Task<StoreCollectionResult> CreateCollectionAsync(string name, bool overwrite, IHttpContext httpContext)
    {
        string childPath = _fullPath.TrimEnd('/') + "/" + name;
        _store.RunAdb($"shell \"mkdir -p '{childPath}'\"");
        var col = new AdbCollection(_store, childPath, name, DateTime.UtcNow);
        return Task.FromResult(new StoreCollectionResult(DavStatusCode.Created, col));
    }

    public Task<DavStatusCode> DeleteItemAsync(string name, IHttpContext httpContext)
    {
        string childPath = _fullPath.TrimEnd('/') + "/" + name;
        _store.RunAdb($"shell \"rm -rf '{childPath}'\"");
        return Task.FromResult(DavStatusCode.NoContent);
    }

    public Task<Stream> GetReadableStreamAsync(IHttpContext httpContext) => Task.FromResult<Stream>(Stream.Null);
    public Task<DavStatusCode> UploadFromStreamAsync(IHttpContext httpContext, Stream source) => Task.FromResult(DavStatusCode.Forbidden);
    public Task<StoreItemResult> CopyAsync(IStoreCollection destination, string name, bool overwrite, IHttpContext httpContext) => Task.FromResult(new StoreItemResult(DavStatusCode.NotImplemented));
    public bool SupportsFastMove(IStoreCollection destination, string destinationName, bool overwrite, IHttpContext httpContext) => true;
    public Task<StoreItemResult> MoveItemAsync(string sourceName, IStoreCollection destination, string destinationName, bool overwrite, IHttpContext httpContext)
    {
        string src = _fullPath.TrimEnd('/') + "/" + sourceName;
        string dst = destination.UniqueKey.TrimEnd('/') + "/" + destinationName;
        _store.RunAdb($"shell \"mv '{src}' '{dst}'\"");
        return Task.FromResult(new StoreItemResult(DavStatusCode.Created));
    }
}

public class AdbFile : IStoreItem
{
    private readonly AdbStore _store;
    private readonly string _fullPath;
    private readonly string _name;
    public long Size { get; }
    public DateTime Modified { get; }

    public AdbFile(AdbStore store, string fullPath, string name, long size, DateTime modified)
    {
        _store = store;
        _fullPath = fullPath;
        _name = name;
        Size = size;
        Modified = modified;

        PropertyManager = new PropertyManager<AdbFile>(new DavProperty<AdbFile>[]
        {
            new DavCreationDate<AdbFile> { Getter = (c, e) => e.Modified },
            new DavDisplayName<AdbFile> { Getter = (c, e) => e._name },
            new DavGetContentLength<AdbFile> { Getter = (c, e) => e.Size },
            new DavGetContentType<AdbFile> { Getter = (c, e) => "application/octet-stream" },
            new DavGetLastModified<AdbFile> { Getter = (c, e) => e.Modified },
            new DavGetResourceType<AdbFile> { Getter = (c, e) => null },
            new DavLockDiscoveryDefault<AdbFile>(),
            new DavSupportedLockDefault<AdbFile>()
        });
    }

    public string Name => _name;
    public string UniqueKey => _fullPath;
    public IPropertyManager PropertyManager { get; }
    public ILockingManager LockingManager => _store.LockingManager;

    public Task<Stream> GetReadableStreamAsync(IHttpContext httpContext)
    {
        string safeFileName = string.Join("_", _name.Split(Path.GetInvalidFileNameChars()));
        string localCacheFile = Path.Combine(_store.CacheDir, $"cache_{Math.Abs(_fullPath.GetHashCode())}_{safeFileName}");

        bool needPull = !File.Exists(localCacheFile) || (Size > 0 && new FileInfo(localCacheFile).Length != Size);
        if (needPull)
        {
            bool ok = _store.RunAdbTransfer($"pull \"{_fullPath}\" \"{localCacheFile}\"", 180000);
            if (!ok || !File.Exists(localCacheFile))
            {
                return Task.FromResult<Stream>(Stream.Null);
            }
        }

        var stream = new FileStream(localCacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        return Task.FromResult<Stream>(stream);
    }

    public async Task<DavStatusCode> UploadFromStreamAsync(IHttpContext httpContext, Stream source)
    {
        string safeFileName = string.Join("_", _name.Split(Path.GetInvalidFileNameChars()));
        string localTempFile = Path.Combine(_store.CacheDir, $"upload_{Guid.NewGuid():N}_{safeFileName}");

        try
        {
            using (var fs = new FileStream(localTempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(fs);
            }

            bool ok = _store.RunAdbTransfer($"push \"{localTempFile}\" \"{_fullPath}\"", 180000);
            return ok ? DavStatusCode.Ok : DavStatusCode.InternalServerError;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Upload error: " + ex.Message);
            return DavStatusCode.InternalServerError;
        }
        finally
        {
            try { if (File.Exists(localTempFile)) File.Delete(localTempFile); } catch { }
        }
    }

    public Task<StoreItemResult> CopyAsync(IStoreCollection destination, string name, bool overwrite, IHttpContext httpContext)
    {
        string dst = destination.UniqueKey.TrimEnd('/') + "/" + name;
        _store.RunAdb($"shell \"cp '{_fullPath}' '{dst}'\"");
        return Task.FromResult(new StoreItemResult(DavStatusCode.Created));
    }
}

public class PhoneStorageService
{
    private static readonly Lazy<PhoneStorageService> _instance = new(() => new PhoneStorageService());
    public static PhoneStorageService Instance => _instance.Value;

    private readonly EngineService _engine = EngineService.Instance;
    private HttpListener? _listener;
    private WebDavDispatcher? _dispatcher;
    private CancellationTokenSource? _serverCts;

    public bool IsMounted { get; private set; }
    public char? MountedDriveLetter { get; private set; }
    public string? MountedSerial { get; private set; }
    public string? MountedModel { get; private set; }
    public int ServerPort { get; } = 8990;

    public event Action<bool>? OnMountStateChanged;

    private PhoneStorageService()
    {
        AppDomain.CurrentDomain.ProcessExit += (s, e) => UnmountDrive();
    }

    public static char? GetNextAvailableDriveLetter()
    {
        try
        {
            var existingDrives = DriveInfo.GetDrives()
                .Where(d => !string.IsNullOrEmpty(d.Name))
                .Select(d => char.ToUpperInvariant(d.Name[0]))
                .ToHashSet();

            char[] candidates = new[] { 'M', 'P', 'Z', 'Y', 'X', 'W', 'V', 'U', 'T', 'S', 'R' };
            foreach (var c in candidates)
            {
                if (!existingDrives.Contains(c)) return c;
            }
        }
        catch { }
        return 'Z';
    }

    public async Task<bool> MountDriveAsync(string serial, string model)
    {
        if (IsMounted && MountedSerial == serial && MountedDriveLetter.HasValue)
        {
            OpenInExplorer();
            return true;
        }

        UnmountDrive();

        char? letter = GetNextAvailableDriveLetter();
        if (!letter.HasValue)
        {
            return false;
        }

        try
        {
            EnsureWebClientSettings();

            var store = new AdbStore(serial, _engine.AdbPath);
            _dispatcher = new WebDavDispatcher(store, null);

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{ServerPort}/");
            _listener.Prefixes.Add($"http://localhost:{ServerPort}/");
            _listener.Start();

            _serverCts = new CancellationTokenSource();
            _ = RunServerLoopAsync(_listener, _dispatcher, _serverCts.Token);

            string uncPath = $@"\\localhost@{ServerPort}\DavWWWRoot";
            await RunProcessAsync("cmd.exe", $"/c net use {letter.Value}: \"{uncPath}\" /persistent:no");

            try
            {
                string regPath = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2\##localhost@{ServerPort}#DavWWWRoot";
                using var key = Registry.CurrentUser.CreateSubKey(regPath);
                key?.SetValue("_LabelFromDesktop", $"MIRRIVO Phone ({model})");
            }
            catch { }

            IsMounted = true;
            MountedDriveLetter = letter.Value;
            MountedSerial = serial;
            MountedModel = model;

            OnMountStateChanged?.Invoke(true);

            OpenInExplorer();

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("MountDriveAsync error: " + ex.Message);
            UnmountDrive();
            return false;
        }
    }

    public void UnmountDrive()
    {
        if (MountedDriveLetter.HasValue)
        {
            char letter = MountedDriveLetter.Value;
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c net use {letter}: /delete /y",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                p?.WaitForExit(3000);
            }
            catch { }
        }

        try
        {
            _serverCts?.Cancel();
            _serverCts?.Dispose();
            _serverCts = null;
        }
        catch { }

        try
        {
            _listener?.Stop();
            _listener?.Close();
            _listener = null;
        }
        catch { }

        _dispatcher = null;
        IsMounted = false;
        MountedDriveLetter = null;
        MountedSerial = null;
        MountedModel = null;

        OnMountStateChanged?.Invoke(false);
    }

    public void OpenInExplorer()
    {
        if (MountedDriveLetter.HasValue)
        {
            try
            {
                Process.Start("explorer.exe", $"{MountedDriveLetter.Value}:\\");
            }
            catch { }
        }
    }

    public async Task<bool> PullFileDirectAsync(string remotePath, string localDestPath)
    {
        if (string.IsNullOrEmpty(MountedSerial)) return false;
        return await Task.Run(() =>
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _engine.AdbPath,
                        Arguments = $"-s {MountedSerial} pull \"{remotePath}\" \"{localDestPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                p.Start();
                p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                return p.WaitForExit(180000) && p.ExitCode == 0;
            }
            catch { return false; }
        });
    }

    public async Task<bool> PushFileDirectAsync(string localPath, string remoteDestPath)
    {
        if (string.IsNullOrEmpty(MountedSerial)) return false;
        return await Task.Run(() =>
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _engine.AdbPath,
                        Arguments = $"-s {MountedSerial} push \"{localPath}\" \"{remoteDestPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                p.Start();
                p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                return p.WaitForExit(180000) && p.ExitCode == 0;
            }
            catch { return false; }
        });
    }

    private async Task RunServerLoopAsync(HttpListener listener, WebDavDispatcher dispatcher, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            try
            {
                var context = await listener.GetContextAsync();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var httpContext = new HttpContext(context);
                        await dispatcher.DispatchRequestAsync(httpContext);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("WebDAV dispatch error: " + ex.Message);
                    }
                }, ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("WebDAV server loop error: " + ex.Message);
            }
        }
    }

    private static void EnsureWebClientSettings()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WebClient\Parameters", true);
            if (key != null)
            {
                var auth = key.GetValue("BasicAuthLevel");
                if (auth == null || (int)auth < 2)
                {
                    key.SetValue("BasicAuthLevel", 2, RegistryValueKind.DWord);
                }

                var sizeLimit = key.GetValue("FileSizeLimitInBytes");
                if (sizeLimit == null || Convert.ToInt64(sizeLimit) < 4294967295)
                {
                    key.SetValue("FileSizeLimitInBytes", 4294967295, RegistryValueKind.DWord);
                }
            }
        }
        catch { }
    }

    private static Task RunProcessAsync(string fileName, string args)
    {
        var tcs = new TaskCompletionSource<bool>();
        try
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            p.Exited += (s, e) =>
            {
                tcs.TrySetResult(p.ExitCode == 0);
                p.Dispose();
            };
            p.Start();
        }
        catch
        {
            tcs.TrySetResult(false);
        }
        return tcs.Task;
    }
}
