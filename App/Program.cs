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
            .OnBeforeUninstallFastCallback(_ =>
            {
                new Services.MonitorClient(Monitoring.MonitorData.DefaultRoot).Stop().GetAwaiter().GetResult();
                Services.MonitorClient.RemoveLogin(); Services.DesktopHost.RemoveDefaultLogin();
            })
            .OnBeforeUpdateFastCallback(_ =>
            {
                Monitoring.SteamBridge.RequireIdle();
                new Services.MonitorClient(Monitoring.MonitorData.DefaultRoot).Stop().GetAwaiter().GetResult();
            })
            .Run();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
