using AstralStation.Monitoring;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

string[] arguments = Environment.GetCommandLineArgs();
if (arguments.Contains("--steam-bridge"))
{
    int configAt = Array.IndexOf(arguments, "--config");
    if (configAt < 0 || configAt + 1 >= arguments.Length) { Environment.ExitCode = 1; return; }
    string config = Path.GetFullPath(arguments[configAt + 1]);
    try { await new SteamSession(config).Run(); }
    catch (Exception ex) { SteamBridge.ReportError(config, ex); Environment.ExitCode = 1; }
    return;
}
int at = Array.IndexOf(arguments, "--data-dir");
string root = at >= 0 && at + 1 < arguments.Length ? Path.GetFullPath(arguments[at + 1]) : MonitorData.DefaultRoot;
string identity = MonitorData.Identity(root);
// A named mutex stays owned by this thread until the process exits; async continuations never release it.
using var singleton = new Mutex(true, "Local\\" + identity, out bool first);
if (!first) return;
using var cancellation = new CancellationTokenSource();
var engine = new MonitorEngine(root);
var server = Serve();
try { await engine.Run(cancellation.Token); }
catch (Exception ex) { engine.Error = ex.Message; }
finally { cancellation.Cancel(); try { await server; } catch (OperationCanceledException) { } }

async Task Serve()
{
    while (!cancellation.IsCancellationRequested)
    {
        using var pipe = new NamedPipeServerStream(identity, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.WaitForConnectionAsync(cancellation.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            // One-byte commands keep the local protocol bounded. Other users cannot connect.
            byte[] request = new byte[1];
            if (await pipe.ReadAsync(request, timeout.Token) != 1) continue;
            if (request[0] is not (1 or 2)) continue;
            byte[] reply = JsonSerializer.SerializeToUtf8Bytes(engine.Status);
            await pipe.WriteAsync(reply, timeout.Token); await pipe.FlushAsync(timeout.Token);
            if (request[0] == 2) cancellation.Cancel();
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        catch (IOException) { }
    }
}

internal sealed class MonitorEngine(string root)
{
    private readonly string activeFile = Path.Combine(MonitorData.DirectoryAt(root), "active.json");
    private List<MonitorRecord> active = [];
    private DateTimeOffset checkpoint;
    private bool loaded, recovered;
    private readonly SteamLinkController steam = new();
    private readonly int sessionId = CurrentSession();
    private static int CurrentSession() { using var current = Process.GetCurrentProcess(); return current.SessionId; }
    public string? Error { get; set; }
    private readonly string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public MonitorStatus Status => new(version, Environment.ProcessId, DateTimeOffset.Now, active.Count, Error, steam.State);
    public async Task Run(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try
                {
                    if (!loaded)
                    {
                        active = File.Exists(activeFile) ? MonitorData.Read<List<MonitorRecord>>(activeFile) : [];
                        foreach (var record in active.ToArray())
                        {
                            record.Game.Validate(); string file = MonitorData.RecordPath(root, record);
                            // Finish may have committed just before a crash interrupted the active checkpoint.
                            if (File.Exists(file) && MonitorData.Read<MonitorRecord>(file).EndedAt is not null) active.Remove(record);
                        }
                        loaded = true;
                    }
                    var config = MonitorData.ReadConfig(root);
                    if (!config.Enabled) break;
                    await Tick(config); Error = null;
                }
                catch (Exception ex) { Error = "监测暂停，可在设置检查数据或权限：" + ex.Message; }
            } while (await timer.WaitForNextTickAsync(cancellation));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (loaded)
            {
                if (!MonitorData.ReadConfig(root).Enabled)
                    foreach (var record in active.ToArray()) Finish(record, record.LastSeenAt, "已停止监测");
                Checkpoint();
            }
        }
    }
    private async Task Tick(MonitorConfig config)
    {
        var now = DateTimeOffset.Now;
        var found = new Dictionary<(string Game, int Pid, long Created), MonitorGame>();
        var uncertain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in config.Games.Concat(active.Select(r => r.Game)).DistinctBy(g => g.Id).GroupBy(g => g.ProcessName, StringComparer.OrdinalIgnoreCase))
        foreach (var process in Process.GetProcessesByName(group.Key))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId) continue;
                    var handle = OpenProcess(0x1000, false, process.Id);
                    if (handle == 0) { if (!process.HasExited) uncertain.Add(group.Key); continue; }
                    try
                    {
                        var path = new StringBuilder(32768); int length = path.Capacity;
                        if (!QueryFullProcessImageNameW(handle, 0, path, ref length) || !GetProcessTimes(handle, out long created, out _, out _, out _))
                        { uncertain.Add(group.Key); continue; }
                        string actualPath = path.ToString();
                        var game = group.Where(g => config.Games.Any(selected => selected.Id == g.Id) && (g.Path.Length == 0 || string.Equals(g.Path, actualPath, StringComparison.OrdinalIgnoreCase)))
                            .OrderBy(g => g.Path.Length == 0).ThenBy(g => g.FromQueue).FirstOrDefault();
                        if (game is not null)
                            found[(game.Id, process.Id, created)] = new MonitorGame { Id = game.Id, Path = actualPath, ProcessName = game.ProcessName, DisplayName = game.DisplayName, QueueTaskId = game.QueueTaskId };
                    }
                    finally { CloseHandle(handle); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { uncertain.Add(group.Key); }
            }
        }
        bool changed = false;
        foreach (var record in active.ToArray())
        {
            if (!config.Games.Any(g => g.Id == record.Game.Id && (g.Path.Length == 0 || string.Equals(g.Path, record.Game.Path, StringComparison.OrdinalIgnoreCase)) && string.Equals(g.ProcessName, record.Game.ProcessName, StringComparison.OrdinalIgnoreCase)))
            { Finish(record, now, "已移出或修改监测清单"); changed = true; continue; }
            if (found.ContainsKey((record.Game.Id, record.ProcessId, record.CreatedUtcTicks))) { record.LastSeenAt = now; continue; }
            if (uncertain.Contains(record.Game.ProcessName)) continue;
            // A crash/reboot has no exact exit event. Preserve the last observed time, never invent offline playtime.
            Finish(record, recovered ? now : record.LastSeenAt, recovered ? "进程退出" : "监测中断后恢复，退出时间按最后观测记录"); changed = true;
        }
        foreach (var pair in found)
        {
            if (!config.Games.Any(g => g.Id == pair.Key.Game) || active.Any(r => r.Game.Id == pair.Key.Game && r.ProcessId == pair.Key.Pid && r.CreatedUtcTicks == pair.Key.Created)) continue;
            var record = new MonitorRecord { Game = pair.Value, ProcessId = pair.Key.Pid, CreatedUtcTicks = pair.Key.Created, StartedAt = now, LastSeenAt = now };
            MonitorData.Atomic(MonitorData.RecordPath(root, record), record); active.Add(record); changed = true;
        }
        recovered = true;
        if (changed || now - checkpoint >= TimeSpan.FromSeconds(10)) Checkpoint();
        await steam.Tick(config, found.Values.ToArray(), uncertain.Contains("Endfield"), now);
        if (uncertain.Count > 0) throw new IOException("无法确认进程路径或身份：" + string.Join("、", uncertain) + "。不会误记为游戏退出。");
    }
    private void Finish(MonitorRecord record, DateTimeOffset end, string reason)
    {
        record.EndedAt = end; record.EndReason = reason;
        MonitorData.Atomic(MonitorData.RecordPath(root, record), record); active.Remove(record);
    }
    private void Checkpoint() { MonitorData.Atomic(activeFile, active); checkpoint = DateTimeOffset.Now; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(nint handle, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(nint handle, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
