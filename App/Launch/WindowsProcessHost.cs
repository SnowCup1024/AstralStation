using System.Diagnostics;
using System.Runtime.InteropServices;


namespace AstralStation.Launch;

public sealed class WindowsProcessHost
{
    public sealed record ProcessEntry(int Id, int ParentId, string Name, long Created, string Path);
    public sealed record DiscoveryResult(List<string> Names, string Notice, string? ExactMatch = null);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    public List<ProcessEntry> Snapshot(bool details = false, Func<int, bool>? needsDetails = null)
    {
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>(), ExeFile = "" };
            if (!Process32FirstW(handle, ref entry)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            List<ProcessEntry> result = [];
            do
            {
                long created = 0; string path = "";
                if (details && (needsDetails is null || needsDetails((int)entry.ProcessId)))
                {
                    try
                    {
                        using var process = Process.GetProcessById((int)entry.ProcessId);
                        created = process.StartTime.ToUniversalTime().Ticks;
                        try { path = process.MainModule?.FileName ?? ""; } catch (System.ComponentModel.Win32Exception) { }
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                result.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId, LaunchTask.Normalize(entry.ExeFile), created, path));
            } while (Process32NextW(handle, ref entry));
            int error = Marshal.GetLastWin32Error();
            if (error != 18) throw new System.ComponentModel.Win32Exception(error);
            return result;
        }
        finally { CloseHandle(handle); }
    }
    public HashSet<string> Names() => Snapshot().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public void Launch(string path) { using var process = Start(path, false); }
    public void Launch(LaunchTask task) { using var process = Start(task.Path, false, task.Arguments, task.WorkingDirectory); }
    private static Process? Start(string path, bool hidden, string arguments = "", string directory = "")
    {
        if (!File.Exists(path)) throw new FileNotFoundException("目标文件不存在：" + path);
        path = System.IO.Path.GetFullPath(path);
        directory = string.IsNullOrWhiteSpace(directory) ? System.IO.Path.GetDirectoryName(path)! : System.IO.Path.GetFullPath(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("工作目录不存在。");
        var info = new ProcessStartInfo(path) { UseShellExecute = true, Arguments = arguments, WorkingDirectory = directory, WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal };
        switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
        {
            case ".ps1":
                info.FileName = System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell/v1.0/powershell.exe");
                info.Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass " + (hidden ? "-WindowStyle Hidden " : "") + "-File \"" + path + "\" " + arguments;
                break;
            case ".bat": case ".cmd":
                info.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
                info.Arguments = "/d /s /c \"\"" + path + "\" " + arguments + "\"";
                break;
        }
        return Process.Start(info);
    }
    public async Task<DiscoveryResult> DiscoverAsync(string path, IProgress<string> progress, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        progress.Report("正在启动并识别 · 剩余 15 秒");
        return await Task.Run(async () =>
        {
            path = System.IO.Path.GetFullPath(path);
            string expectedName = System.IO.Path.GetFileNameWithoutExtension(path);
            string? exactMatch = null;
            // Never inspect modules of every existing process on each polling cycle.
            // Excluding baseline PIDs also conservatively excludes any later reuse of those IDs.
            var baselineIds = Snapshot().Select(p => p.Id).ToHashSet();
            Dictionary<(int, long), ProcessEntry> known = [];
            List<string> warnings = [];
            Exception? failure = null;
            void Extend()
            {
                var current = Snapshot(true, id => !baselineIds.Contains(id));
                var byId = current.ToDictionary(p => p.Id);
                bool changed;
                do
                {
                    changed = false;
                    foreach (var p in current)
                    {
                        if (p.Created == 0 || baselineIds.Contains(p.Id) || known.ContainsKey((p.Id, p.Created))) continue;
                        bool child = known.Values.Any(parent => parent.Id == p.ParentId && parent.Created <= p.Created &&
                            (!byId.TryGetValue(parent.Id, out var liveParent) || liveParent.Created == parent.Created));
                        if (string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase) || child) { known[(p.Id, p.Created)] = p; changed = true; }
                    }
                } while (changed);
            }
            try
            {
                cancellation.ThrowIfCancellationRequested();
                using var root = Start(path, true);
                clock.Restart();
                if (root is not null)
                {
                    try
                    {
                        var identity = (root.Id, root.StartTime.ToUniversalTime().Ticks);
                        if (!baselineIds.Contains(root.Id)) known[identity] = new(root.Id, 0, root.ProcessName, identity.Item2, path);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                exactMatch = known.Values.Select(p => LaunchTask.Normalize(p.Name))
                    .FirstOrDefault(n => n.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
                while (exactMatch is null && clock.Elapsed.TotalSeconds < 15)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Extend();
                    exactMatch = known.Values.Select(p => LaunchTask.Normalize(p.Name))
                        .FirstOrDefault(n => n.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
                    if (exactMatch is not null) break;
                    double remaining = Math.Max(0, 15 - clock.Elapsed.TotalSeconds);
                    progress.Report($"识别中 · 剩余 {Math.Ceiling(remaining)} 秒 · 已找到 {known.Values.Select(p => p.Name).Distinct().Count()} 个进程");
                    if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(0.2, remaining)), cancellation);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failure = ex; }
            finally
            {
                progress.Report(cancellation.IsCancellationRequested ? "正在取消并清理临时进程…" : "识别结束，正在清理临时进程…");
                try { Extend(); } catch (Exception ex) { warnings.Add("结束时读取进程失败：" + ex.Message); }
                foreach (var p in known.Values.OrderByDescending(p => p.Created))
                {
                    try
                    {
                        var handle = OpenProcess(0x1000 | 1 | 0x100000, false, p.Id);
                        if (handle == IntPtr.Zero) { if (Marshal.GetLastWin32Error() != 87) warnings.Add("无法清理进程 " + p.Name + "，请手动检查。"); continue; }
                        try
                        {
                            // Validate and terminate through the same open handle, never a newly opened PID.
                            if (GetProcessTimes(handle, out long created, out _, out _, out _) && DateTime.FromFileTimeUtc(created).Ticks == p.Created && WaitForSingleObject(handle, 0) != 0)
                                if (!TerminateProcess(handle, 0)) warnings.Add("无法结束 " + p.Name + "，请手动检查。");
                        }
                        finally { CloseHandle(handle); }
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (Exception ex) { warnings.Add("无法结束 " + p.Name + "：" + ex.Message); }
                }
            }
            var names = known.Values.Select(p => LaunchTask.Normalize(p.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (failure is not null) throw new InvalidOperationException(failure.Message + "\n" + string.Join("\n", warnings), failure);
            exactMatch ??= names.FirstOrDefault(n => n.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
            return new DiscoveryResult(names, string.Join("\n", warnings), exactMatch);
        }, CancellationToken.None);
    }
}
