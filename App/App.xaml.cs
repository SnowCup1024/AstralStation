using Microsoft.UI.Xaml;
using System.Globalization;
using AstralStation.Services;

namespace AstralStation;

public partial class App : Application
{
    private MainWindow? window;
    private Mutex? mutex;
    private string? dataRoot;
    public App()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        InitializeComponent();
        UnhandledException += (_, e) => { try { if (dataRoot is not null) { Directory.CreateDirectory(dataRoot); File.AppendAllText(Path.Combine(dataRoot, "crash.log"), DateTime.Now + "\n" + e.Exception + "\n"); } } catch { } };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AstralStation", "Launch");
        int index = Array.IndexOf(arguments, "--data-dir");
        if (index >= 0 && index + 1 < arguments.Length) root = Path.GetFullPath(arguments[index + 1]);
        dataRoot = root;
        mutex = new Mutex(true, "Local\\" + DesktopHost.Identity(root), out bool first);
        if (!first) { DesktopHost.ActivateExisting(root); mutex.Dispose(); Exit(); return; }
        window = new MainWindow(root, arguments.Contains("--paused"), arguments.Contains("--background"));
        window.Closed += (_, _) => { mutex.ReleaseMutex(); mutex.Dispose(); };
        window.Activate();
    }
}
