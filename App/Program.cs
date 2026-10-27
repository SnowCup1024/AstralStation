using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace AstralStation;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Installer hooks must run before WinUI, the instance mutex or user data are opened.
        VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => Services.DesktopHost.RemoveDefaultLogin())
            .Run();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
