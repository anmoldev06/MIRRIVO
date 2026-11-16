using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace Mirrivo.Services;

public class EngineService
{
    private static readonly Lazy<EngineService> _instance = new(() => new EngineService());
    public static EngineService Instance => _instance.Value;

    public string EngineDirectory { get; private set; } = string.Empty;
    public string AdbPath { get; private set; } = string.Empty;
    public string ScrcpyPath { get; private set; } = string.Empty;
    public bool IsReady => File.Exists(AdbPath) && File.Exists(ScrcpyPath);

    public EngineService()
    {
        Initialize();
    }

    public void Initialize()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        
        // 1. Check if Engine folder exists next to executable
        string localEngine = Path.Combine(baseDir, "Engine");
        if (File.Exists(Path.Combine(localEngine, "scrcpy.exe")) && File.Exists(Path.Combine(localEngine, "adb.exe")))
        {
            EngineDirectory = localEngine;
            AdbPath = Path.Combine(localEngine, "adb.exe");
            ScrcpyPath = Path.Combine(localEngine, "scrcpy.exe");
            return;
        }

        // 2. Check root next to executable
        if (File.Exists(Path.Combine(baseDir, "scrcpy.exe")) && File.Exists(Path.Combine(baseDir, "adb.exe")))
        {
            EngineDirectory = baseDir;
            AdbPath = Path.Combine(baseDir, "adb.exe");
            ScrcpyPath = Path.Combine(baseDir, "scrcpy.exe");
            return;
        }

        // 3. Check LocalAppData directory
        string appDataEngine = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mirrivo",
            "engine"
        );

        if (File.Exists(Path.Combine(appDataEngine, "scrcpy.exe")) && File.Exists(Path.Combine(appDataEngine, "adb.exe")))
        {
            EngineDirectory = appDataEngine;
            AdbPath = Path.Combine(appDataEngine, "adb.exe");
            ScrcpyPath = Path.Combine(appDataEngine, "scrcpy.exe");
            return;
        }

        // 4. Try extracting embedded Engine.zip resource to LocalAppData
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Engine.zip", StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                Directory.CreateDirectory(appDataEngine);
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using ZipArchive archive = new(stream, ZipArchiveMode.Read);
                    archive.ExtractToDirectory(appDataEngine, overwriteFiles: true);

                    EngineDirectory = appDataEngine;
                    AdbPath = Path.Combine(appDataEngine, "adb.exe");
                    ScrcpyPath = Path.Combine(appDataEngine, "scrcpy.exe");
                    return;
                }
            }
        }
        catch
        {
            // Fallback continues below
        }

        // 5. Check WinGet package location as host fallback
        string wingetScrcpyDir = @"C:\Users\Admin\AppData\Local\Microsoft\WinGet\Packages\Genymobile.scrcpy_Microsoft.Winget.Source_8wekyb3d8bbwe\scrcpy-win64-v5.0.1";
        if (Directory.Exists(wingetScrcpyDir) && File.Exists(Path.Combine(wingetScrcpyDir, "scrcpy.exe")))
        {
            EngineDirectory = wingetScrcpyDir;
            AdbPath = Path.Combine(wingetScrcpyDir, "adb.exe");
            ScrcpyPath = Path.Combine(wingetScrcpyDir, "scrcpy.exe");
            return;
        }

        // Fallback default
        EngineDirectory = appDataEngine;
        AdbPath = Path.Combine(appDataEngine, "adb.exe");
        ScrcpyPath = Path.Combine(appDataEngine, "scrcpy.exe");
    }
}
