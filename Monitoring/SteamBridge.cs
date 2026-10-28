using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AstralStation.Monitoring;

public sealed class SteamRuntime
{
    public string Launcher { get; set; } = "";
    public string GameDirectory { get; set; } = "";
    public string Arguments { get; set; } = "--game=endfield --reason=4";
    public int ExitGraceSeconds { get; set; } = 8;
    public int WaitForGameSeconds { get; set; } = 600;
    public bool CloseOwnedLauncher { get; set; } = true;
    public string GamePath => Path.Combine(GameDirectory, "Endfield.exe");
    public static SteamRuntime Read(string file)
    {
        var value = MonitorData.Read<SteamRuntime>(file);
        if (!Path.IsPathFullyQualified(value.GameDirectory) || !Path.IsPathFullyQualified(value.Launcher)
            || !Path.GetFileName(value.Launcher).Equals("Launcher.exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(value.GamePath) || new FileInfo(value.GamePath).Length == 0 || !File.Exists(value.Launcher) || new FileInfo(value.Launcher).Length == 0
            || value.ExitGraceSeconds is < 2 or > 60 || value.WaitForGameSeconds is < 60 or > 3600
            || value.Arguments is null || value.Arguments.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("终末地启动配置无效，请重新在 Steam 启动国服页面复制新版启动选项。");
        value.GameDirectory = Path.GetFullPath(value.GameDirectory); value.Launcher = Path.GetFullPath(value.Launcher);
        return value;
    }
}
public sealed record ProcessIdentity(int Id, long Created, string Path);
public sealed record BridgeStatus(int Protocol, int ProcessId, long Created, string Executable, string GamePath, string Session, bool SeenGame, string State, string? Error);
internal sealed record SteamAttachRequest(int Schema, string Id, string GamePath, DateTimeOffset RequestedAt);

public static class SteamBridge
{
    public const string AppId = "4732690";
    public const string GateName = @"Local\AstralStation.SteamLaunch.4732690";
    public static string DefaultConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AstralStation", "SteamLaunch", AppId, "config.json");
    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory,
        File.Exists(Path.Combine(AppContext.BaseDirectory, "AstralStation.Monitor.exe")) ? "" : "Monitor", "AstralStation.Monitor.exe");
    public static string PipeName(string config) => MonitorData.Identity(Path.GetFullPath(config)) + ".SteamBridge";
    public static string Options(string executable, string config) => Quote(executable) + " --steam-bridge --config " + Quote(config) + " %command%";
    private static string AttachPath(string config) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config))!, "attach-request.json");
    internal static string RequestAttach(string config, string gamePath)
    {
        string id = Guid.NewGuid().ToString("N");
        AstralStation.SteamLaunch.NoLinks(AttachPath(config));
        MonitorData.Atomic(AttachPath(config), new SteamAttachRequest(1, id, Path.GetFullPath(gamePath), DateTimeOffset.Now));
        return id;
    }
    internal static bool TakeAttach(string config, string gamePath)
    {
        string file = AttachPath(config);
        AstralStation.SteamLaunch.NoLinks(file);
        if (!File.Exists(file)) return false;
        // Even a delayed Steam launch must remain attach-only. Never expire an automatic
        // request into permission to open a launcher; malformed requests also fail closed.
        var request = MonitorData.Read<SteamAttachRequest>(file);
        if (request.Schema != 1 || !Guid.TryParseExact(request.Id, "N", out _) || request.RequestedAt == default
            || !string.Equals(request.GamePath, gamePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("自动联动请求无效，已停止等待，不会打开启动器。");
        File.Delete(file);
        return true;
    }
    internal static void CancelAttach(string config, string id)
    {
        string file = AttachPath(config);
        AstralStation.SteamLaunch.NoLinks(file);
        if (File.Exists(file) && MonitorData.Read<SteamAttachRequest>(file).Id == id) File.Delete(file);
    }
    public static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value) { if (c == '\\') { slashes++; continue; } result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0; }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static bool Running
    {
        get
        {
            if (!Mutex.TryOpenExisting(GateName, out var mutex)) return false;
            using (mutex)
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (acquired) mutex.ReleaseMutex();
                return !acquired;
            }
        }
    }
    public static void RequireIdle()
    {
        if (Running) throw new InvalidOperationException("Steam 等待进程仍在运行。请在游戏结束或取消启动等待后安装更新、修改启动配置。");
    }
    public static ProcessIdentity? Identity(int pid)
    {
        nint handle = OpenProcess(0x1000, false, pid);
        if (handle == 0) return null;
        try
        {
            var path = new StringBuilder(32768); int length = path.Capacity;
            return QueryFullProcessImageNameW(handle, 0, path, ref length) && GetProcessTimes(handle, out long created, out _, out _, out _)
                ? new(pid, created, path.ToString()) : null;
        }
        finally { CloseHandle(handle); }
    }
    public static (List<ProcessIdentity> Processes, bool Uncertain) Find(string path)
    {
        var found = new List<ProcessIdentity>(); bool uncertain = false;
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        using (process)
        {
            try
            {
                if (process.SessionId != current.SessionId) continue;
                var identity = Identity(process.Id);
                if (identity is null) { if (!process.HasExited) uncertain = true; }
                else if (string.Equals(identity.Path, path, StringComparison.OrdinalIgnoreCase)) found.Add(identity);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { if (!SafeExited(process)) uncertain = true; }
        }
        return (found, uncertain);
    }
    private static bool SafeExited(Process process) { try { return process.HasExited; } catch { return false; } }
    public static async Task<BridgeStatus?> Query(string config, bool cancelWaiting = false)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            using var pipe = new NamedPipeClientStream(".", PipeName(config), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) return null;
            var identity = Identity((int)pid);
            if (identity is null || !string.Equals(identity.Path, ExecutablePath, StringComparison.OrdinalIgnoreCase)) return null;
            await pipe.WriteAsync(new byte[] { cancelWaiting ? (byte)2 : (byte)1 }, timeout.Token);
            var state = await JsonSerializer.DeserializeAsync<BridgeStatus>(pipe, cancellationToken: timeout.Token);
            if (state is null || state.Protocol != 1 || state.ProcessId != pid || identity.Created != state.Created
                || !string.Equals(identity.Path, state.Executable, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(identity.Path, ExecutablePath, StringComparison.OrdinalIgnoreCase)) return null;
            return state;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or JsonException) { return null; }
    }
    public static void ReportError(string config, Exception error)
    {
        try { MonitorData.Atomic(Path.Combine(Path.GetDirectoryName(config)!, "bridge-error.json"), new { Time = DateTimeOffset.Now, Message = error.Message }); } catch { }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(nint handle, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(nint handle, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
