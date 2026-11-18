using System.IO;
using System.Windows;
using Mirrivo.Services;

namespace Mirrivo;

public partial class App : Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), e.ExceptionObject?.ToString() ?? "Unknown exception");
            }
            catch { }
        };

        DispatcherUnhandledException += (s, e) =>
        {
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), e.Exception?.ToString() ?? "Unknown dispatcher exception");
            }
            catch { }
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            MirrorService.Instance.StopAll();
        }
        catch { }

        base.OnExit(e);
    }
}
