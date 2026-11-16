using System.Windows;
using Mirrivo.Services;

namespace Mirrivo;

public partial class App : Application
{
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
