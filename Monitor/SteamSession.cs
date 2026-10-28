using AstralStation;
using AstralStation.Monitoring;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

internal sealed class SteamSession(string configPath)
{
    private SteamRuntime runtime = null!;
    private ProcessIdentity identity = null!;
    private readonly string session = Guid.NewGuid().ToString("N");
    private volatile bool seen;
    private string state = "等待游戏启动";
    private string? error;
    private BridgeStatus Status => new(1, identity.Id, identity.Created, identity.Path, runtime.GamePath, session, seen, state, error);
    public async Task Run()
    {
        // The initial thread owns the gate until this WinExe process exits. No release on another async thread.
        using var gate = new Mutex(true, SteamBridge.GateName, out bool first);
        if (!first)
        {
            bool acquired; try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
        }
        using var cancellation = new CancellationTokenSource();
        Process? ownedLauncher = null; Task? server = null;
        try
        {
            runtime = SteamRuntime.Read(configPath);
            identity = SteamBridge.Identity(Environment.ProcessId) ?? throw new IOException("无法读取 Steam 等待进程身份。");
            server = Serve(cancellation);
            var initial = SteamBridge.Find(runtime.GamePath);
            seen = initial.Processes.Count > 0;
            bool requestedLauncher = false;
            var startedAt = Stopwatch.StartNew(); double? absentAt = null;
            void OpenLauncher()
            {
                requestedLauncher = true; startedAt.Restart();
                var launchers = SteamBridge.Find(runtime.Launcher);
                var started = Process.Start(new ProcessStartInfo(runtime.Launcher) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(runtime.Launcher)!, Arguments = runtime.Arguments });
                if (launchers.Processes.Count == 0 && !launchers.Uncertain) ownedLauncher = started;
                else started?.Dispose();
            }
            if (!seen && !initial.Uncertain) OpenLauncher();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            do
            {
                var game = SteamBridge.Find(runtime.GamePath);
                if (game.Processes.Count > 0) { seen = true; absentAt = null; state = "游戏运行中"; error = null; }
                else if (game.Uncertain) { absentAt = null; error = "无法读取游戏进程身份，保持等待，请检查游戏与 Steam 的权限。"; }
                else if (seen)
                {
                    absentAt ??= startedAt.Elapsed.TotalSeconds; state = "等待游戏退出确认"; error = null;
                    if (startedAt.Elapsed.TotalSeconds - absentAt >= runtime.ExitGraceSeconds) break;
                }
                else
                {
                    // If an initially inaccessible process disappears, launch only after its absence is confirmed.
                    if (!requestedLauncher) OpenLauncher();
                    error = null;
                    if (startedAt.Elapsed.TotalSeconds >= runtime.WaitForGameSeconds)
                    { SteamBridge.ReportError(configPath, new TimeoutException("等待游戏启动超时，Steam 等待进程已退出。")); break; }
                }
            } while (await timer.WaitForNextTickAsync(cancellation.Token));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { SteamBridge.ReportError(configPath, ex); }
        finally
        {
            cancellation.Cancel();
            if (server is not null) try { await server; } catch (OperationCanceledException) { }
            if (ownedLauncher is not null)
            {
                try
                {
                    var game = SteamBridge.Find(runtime.GamePath);
                    if (runtime.CloseOwnedLauncher && game.Processes.Count == 0 && !game.Uncertain && !ownedLauncher.HasExited) ownedLauncher.CloseMainWindow();
                }
                catch { }
                ownedLauncher.Dispose();
            }
        }
    }
    private async Task Serve(CancellationTokenSource cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(SteamBridge.PipeName(configPath), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellation.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] command = new byte[1]; if (await pipe.ReadAsync(command, timeout.Token) != 1) continue;
                bool cancel = false;
                if (command[0] == 2 && !seen)
                {
                    var game = SteamBridge.Find(runtime.GamePath);
                    cancel = game.Processes.Count == 0 && !game.Uncertain;
                    if (cancel) state = "已取消启动等待";
                }
                await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(Status), timeout.Token); await pipe.FlushAsync(timeout.Token);
                if (cancel) cancellation.Cancel();
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }
}

internal sealed class SteamLinkController(Action<string>? request = null, Func<MonitorConfig>? readConfig = null)
{
    private DateTimeOffset? detected;
    private DateTimeOffset retryAfter;
    private int attempts;
    public string? State { get; private set; }
    public async Task Tick(MonitorConfig config, IReadOnlyCollection<MonitorGame> present, bool uncertain, DateTimeOffset now, CancellationToken cancellation = default)
    {
        if (!config.SteamLinkEnabled) { Reset(); return; }
        try
        {
            var runtime = SteamRuntime.Read(config.SteamLinkConfigPath);
            bool selected = config.Games.Any(g => string.Equals(g.Path, runtime.GamePath, StringComparison.OrdinalIgnoreCase) && g.ProcessName.Equals("Endfield", StringComparison.OrdinalIgnoreCase));
            if (!selected) { Reset(); State = "Steam 联动暂停：实际游戏路径已改变，请重新开启联动以同步监测。"; return; }
            bool running = present.Any(g => string.Equals(g.Path, runtime.GamePath, StringComparison.OrdinalIgnoreCase));
            if (!running) { if (!uncertain) Reset(); State = "Steam 联动已就绪，等待终末地运行。"; return; }
            var bridge = await SteamBridge.Query(config.SteamLinkConfigPath);
            if (bridge is not null) { State = bridge.Error ?? "Steam 等待进程持续运行中 · " + bridge.State; return; }
            if (SteamBridge.Running) { State = "已有 Steam 等待进程，正在等待登记；旧版脚本运行期间不重复启动。"; return; }
            if (detected is null)
            {
                detected = now;
                // Preserve the short Steam registration grace without another full process scan.
                await Task.Delay(TimeSpan.FromSeconds(3), cancellation);
                if (SteamBridge.Running) return;
                var confirmed = SteamBridge.Find(runtime.GamePath);
                if (confirmed.Uncertain || confirmed.Processes.Count == 0) return;
                var current = readConfig?.Invoke() ?? config;
                if (!current.Enabled || !current.SteamLinkEnabled || current.SteamLinkConfigPath != config.SteamLinkConfigPath
                    || !current.Games.Any(g => string.Equals(g.Path, runtime.GamePath, StringComparison.OrdinalIgnoreCase))) return;
                now = DateTimeOffset.Now;
            }
            if (attempts >= 2) { State = "Steam 等待进程未出现，本次运行已停止重试。请检查 Steam 启动选项。"; return; }
            if (now < retryAfter) { State = "已请求 Steam 启动，正在等待进程登记…"; return; }
            var setup = new SteamLaunch(Path.GetDirectoryName(config.SteamLinkConfigPath));
            if (!setup.Configured()) { State = "Steam 联动未生效：请在 Steam 游戏属性粘贴新版启动选项，确认 ACF 与空 EXE 已配置。"; return; }
            attempts++; retryAfter = now.AddSeconds(30);
            string uri = "steam://rungameid/" + SteamBridge.AppId;
            if (request is not null) request(uri);
            else using (Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })) { }
            State = "已请求 Steam 启动，等待进程将自动跟随游戏退出。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) { State = "Steam 联动暂停：" + ex.Message; }
    }
    private void Reset() { detected = null; retryAfter = default; attempts = 0; State = null; }
}
