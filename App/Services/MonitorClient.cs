using AstralStation.Monitoring;
using AstralStation.Launch;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace AstralStation.Services;

public sealed class MonitorClient(string root)
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    public string SteamConfigPath
    {
        get
        {
            var arguments = Environment.GetCommandLineArgs();
            return arguments.Contains("--steam-root") && arguments.Contains("--data-dir")
                ? Path.Combine(root, "SteamLaunch", SteamBridge.AppId, "config.json") : SteamBridge.DefaultConfigPath;
        }
    }
    public void SyncQueue(IEnumerable<LaunchTask> tasks)
    {
        bool first = !File.Exists(MonitorData.ConfigPath(root));
        var config = MonitorData.ReadConfig(root);
        var next = config.Games.Where(g => !g.FromQueue).ToList();
        foreach (var task in tasks)
        {
            IEnumerable<string> names = task.ProcessNames.Count > 0 ? task.ProcessNames : task.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? [Path.GetFileNameWithoutExtension(task.Path)] : [];
            foreach (string name in names)
            {
                string path = "";
                // A Steam placeholder is never an actual process target.
                foreach (string candidate in new[] { task.Path, Path.Combine(task.WorkingDirectory.Length > 0 ? task.WorkingDirectory : Path.GetDirectoryName(task.Path) ?? "", name + ".exe") })
                    if (Path.IsPathFullyQualified(candidate) && Path.GetFileNameWithoutExtension(candidate).Equals(name, StringComparison.OrdinalIgnoreCase) && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate) && new FileInfo(candidate).Length > 0)
                    { path = Path.GetFullPath(candidate); break; }
                if (next.Any(g => path.Length > 0 ? string.Equals(g.Path, path, StringComparison.OrdinalIgnoreCase) : g.Path.Length == 0 && g.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                var previous = config.Games.FirstOrDefault(g => g.FromQueue && g.QueueTaskId == task.Id && g.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
                var game = new MonitorGame { Id = previous?.Id ?? Guid.NewGuid().ToString("N"), Path = path, ProcessName = name, DisplayName = task.Name, QueueTaskId = task.Id };
                game.Validate(); next.Add(game);
            }
        }
        string before = JsonSerializer.Serialize(config.Games);
        config.Games = next;
        if (first && next.Count > 0) config.Enabled = true;
        if ((first && next.Count > 0) || before != JsonSerializer.Serialize(next)) MonitorData.SaveConfig(root, config);
    }
    private string Identity => MonitorData.Identity(root);
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "Monitor", "AstralStation.Monitor.exe");
    public async Task<MonitorStatus?> Status()
    {
        try { return await Send(1); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { return null; }
    }
    private async Task<MonitorStatus?> Send(byte command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var pipe = new NamedPipeClientStream(".", Identity, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(new byte[] { command }, timeout.Token); await pipe.FlushAsync(timeout.Token);
        return await JsonSerializer.DeserializeAsync<MonitorStatus>(pipe, cancellationToken: timeout.Token);
    }
    public async Task Stop()
    {
        await lifecycle.WaitAsync();
        try { await StopCore(); }
        finally { lifecycle.Release(); }
    }
    private async Task StopCore()
    {
        var status = await Status(); if (status is null) return;
        await Send(2);
        try
        {
            using var worker = Process.GetProcessById(status.ProcessId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await worker.WaitForExitAsync(timeout.Token);
            if (worker.ExitCode != 0) throw new IOException("后台退出时记录保存失败，请检查监测数据后重试。");
        }
        catch (ArgumentException) { }
    }
    public async Task EnsureStarted()
    {
        await lifecycle.WaitAsync();
        try { await StartCore(); }
        finally { lifecycle.Release(); }
    }
    private async Task StartCore()
    {
        if (!MonitorData.ReadConfig(root).Enabled) return;
        if (!File.Exists(Executable)) throw new FileNotFoundException("后台监测程序缺失，请重新安装。", Executable);
        var status = await Status();
        string version = FileVersionInfo.GetVersionInfo(Executable).ProductVersion ?? "";
        if (status is not null)
        {
            if (DesktopHost.CompareVersions(status.Version, version) >= 0) return;
            await StopCore();
        }
        using var process = Process.Start(new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, ArgumentList = { "--data-dir", root } });
        for (int attempt = 0; attempt < 12; attempt++)
        {
            if (await Status() is not null) return;
            if (process?.HasExited == true) break;
            await Task.Delay(150);
        }
        throw new IOException("后台监测未启动，请检查监测配置与安装目录。");
    }
    public async Task SetEnabled(bool enabled)
    {
        var config = MonitorData.ReadConfig(root);
        if (enabled && !AppUpdates.Installed) throw new InvalidOperationException("请先安装星间驿站，再启用持续后台监测。");
        bool previous = config.Enabled;
        using var registry = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        string? previousCommand = registry.GetValue(Identity) as string;
        config.Enabled = enabled; MonitorData.SaveConfig(root, config);
        try
        {
            if (enabled) { RegisterLogin(); await EnsureStarted(); }
            else { registry.DeleteValue(Identity, false); await Stop(); }
        }
        catch
        {
            var restore = MonitorData.ReadConfig(root); restore.Enabled = previous; MonitorData.SaveConfig(root, restore);
            if (previousCommand is null) registry.DeleteValue(Identity, false); else registry.SetValue(Identity, previousCommand);
            throw;
        }
    }
    public void RegisterLogin()
    {
        if (!AppUpdates.Installed || !MonitorData.ReadConfig(root).Enabled) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key.SetValue(Identity, DesktopHost.Quote(Executable) + " --data-dir " + DesktopHost.Quote(root));
    }
    public static void RemoveLogin()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        string prefix = DesktopHost.Quote(Executable) + " --data-dir ";
        foreach (var name in key?.GetValueNames() ?? [])
            if (name.StartsWith("AstralStation.Monitor-", StringComparison.Ordinal) && key!.GetValue(name) is string command && command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(name, false);
    }
    public static async Task StopForInstaller()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MonitorData.DefaultRoot };
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            string prefix = DesktopHost.Quote(Executable) + " --data-dir ";
            foreach (string name in key?.GetValueNames() ?? [])
            {
                if (!name.StartsWith("AstralStation.Monitor-", StringComparison.Ordinal) || key!.GetValue(name) is not string command
                    || !command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string argument = command[prefix.Length..];
                // Registration always uses Quote. Accept only commands that round-trip exactly.
                string candidate = argument.Trim('"');
                if (Path.IsPathFullyQualified(candidate) && DesktopHost.Quote(candidate) == argument && MonitorData.Identity(candidate) == name) roots.Add(candidate);
            }
        }
        foreach (string dataRoot in roots) await new MonitorClient(dataRoot).Stop();
    }
}
