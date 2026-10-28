using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AstralStation;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--installer-prepare") || arguments.Contains("--installer-uninstall"))
        {
            try
            {
                Monitoring.SteamBridge.RequireIdle();
                Services.MonitorClient.StopForInstaller().GetAwaiter().GetResult();
                if (arguments.Contains("--installer-uninstall"))
                { Services.MonitorClient.RemoveLogin(); Services.DesktopHost.RemoveDefaultLogin(); }
            }
            catch { Environment.ExitCode = 1; }
            return;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
