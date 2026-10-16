using System.Diagnostics;

namespace AstralStation.Modules.Launch;

// All access is serialized by LaunchSession on the UI dispatcher. Running tasks keep immutable snapshots.
public sealed class Scheduler(LaunchStore store, WindowsProcessHost processes)
{
    private sealed class Run(LaunchTask task, double deadline)
    {
        public LaunchTask Task = task.Clone();
        public double Deadline = deadline;
        public bool Running, Recorded, Ended;
        public DateTime? LaunchedAt;
        public string ClearedCycle = "", RecordId = "", RecordDate = "";
        public long Serial;
    }
    public sealed record RunEvent(long Serial, LaunchTask Task, bool Started);
    private readonly Dictionary<string, string> attempted = [];
    private readonly Dictionary<string, Run> manual = [];
    private readonly List<Run> detached = [];
    private readonly List<RunEvent> events = [];
    private Run? active;
    private bool delay, paused;
    private double pausedAt;
    private long serial;
    public bool Launching { get; private set; }
    public bool UserPaused { get; private set; }
    public bool Managing { get; private set; }
    public static double Seconds => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    private static bool Present(LaunchTask task, HashSet<string> names) => task.ProcessNames.Any(names.Contains);
    private LaunchTask? Current(Run run) => store.Tasks.FirstOrDefault(t => t.Id == run.Task.Id && string.Equals(System.IO.Path.GetFullPath(t.Path), System.IO.Path.GetFullPath(run.Task.Path), StringComparison.OrdinalIgnoreCase) && t.ProcessNames.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(run.Task.ProcessNames));
    public void SetPause(bool user, bool management)
    {
        UserPaused = user; Managing = management; bool next = user || management || store.Error is not null;
        if (next && !paused) pausedAt = Seconds;
        if (!next && paused && active is not null && delay) active.Deadline += Seconds - pausedAt;
        paused = next;
    }
    private void Detected(Run run, DateTime now, bool detachedRun = false)
    {
        if (run.LaunchedAt is DateTime launched && !run.Recorded) { run.RecordDate = run.Task.Cycle(launched); run.RecordId = store.RecordLaunch(run.Task, launched); run.Recorded = true; }
        var task = detachedRun ? null : Current(run);
        if (task is not null && run.ClearedCycle != task.Cycle(now) && (!run.Running || !store.Done(task, now))) { store.Mark(task, now); store.Log("检测到进程：" + task.Name); }
        if (!run.Running) { run.Serial = ++serial; events.Add(new(run.Serial, run.Task, true)); }
        run.Running = true;
    }
    private void Finished(Run run, DateTime now)
    {
        if (run.Ended) return;
        if (run.Recorded) store.Finish(run.RecordDate, run.RecordId, now);
        events.Add(new(run.Serial, run.Task, false)); run.Ended = true;
    }
    private void Completed(Run run, DateTime now) { if (Current(run) is { } task) attempted[task.Id] = task.Cycle(now); }
    private void Suppress(Run run) { if (Current(run) is { } task) { run.ClearedCycle = task.Cycle(DateTime.Now); attempted[task.Id] = run.ClearedCycle; } }
    private void Launch(Run run)
    {
        Launching = true;
        try { var started = DateTime.Now; processes.Launch(run.Task); run.LaunchedAt = started; run.Deadline = Seconds + run.Task.TimeoutSeconds; }
        finally { Launching = false; }
    }
    public List<RunEvent> TakeEvents() { var result = events.ToList(); events.Clear(); return result; }
    public void Tick(HashSet<string> names)
    {
        if (Launching || store.Error is not null) return;
        var now = DateTime.Now;
        foreach (var pair in manual.ToArray())
        {
            var run = pair.Value;
            if (Present(run.Task, names)) Detected(run, now);
            else if (run.Running || Seconds >= run.Deadline) { if (run.Running) Finished(run, now); store.Log((run.Running ? "手动启动进程已退出：" : "手动启动检测超时：") + run.Task.Name); Completed(run, now); manual.Remove(pair.Key); }
        }
        foreach (var run in detached.ToArray())
        {
            if (Present(run.Task, names)) Detected(run, now, true);
            else if (run.Running || Seconds >= run.Deadline) { if (run.Running) Finished(run, now); store.Log("原任务监测结束：" + run.Task.Name); detached.Remove(run); }
        }
        if (paused && (active is null || delay)) return;
        if (active is null)
        {
            foreach (var task in store.Tasks.Where(t => t.Enabled && !manual.ContainsKey(t.Id)))
            {
                if (Present(task, names)) { active = new(task, Seconds); delay = false; break; }
                if (!store.Done(task, now) && attempted.GetValueOrDefault(task.Id) != task.Cycle(now)) { active = new(task, Seconds + task.DelaySeconds); delay = true; break; }
            }
        }
        if (active is not { } current) return;
        if (Present(current.Task, names)) { Detected(current, now); delay = false; return; }
        if (current.Running) { Finished(current, now); store.Log("进程已退出：" + current.Task.Name); Completed(current, now); active = null; return; }
        if (Seconds < current.Deadline) return;
        if (delay)
        {
            try { Launch(current); }
            catch (Exception ex) { store.Log("启动失败：" + current.Task.Name + " · " + ex.Message); Completed(current, now); active = null; return; }
            delay = false; store.Log("已发起启动，等待进程：" + current.Task.Name);
        }
        else { store.Log("检测超时，未记录完成：" + current.Task.Name); Completed(current, now); active = null; }
    }
    public void RunNow(LaunchTask task)
    {
        if (Launching || store.Error is not null) throw new InvalidOperationException(store.Error ?? "正在启动程序。");
        var names = processes.Names(); var existing = manual.GetValueOrDefault(task.Id) ?? (active?.Task.Id == task.Id && !delay ? active : null);
        if (existing is not null)
        {
            existing.ClearedCycle = "";
            if (!existing.Running || Present(existing.Task, names)) return;
            Finished(existing, DateTime.Now); if (!manual.Remove(task.Id)) active = null;
        }
        var run = new Run(task, Seconds + task.TimeoutSeconds);
        if (Present(task, names)) Detected(run, DateTime.Now);
        else
        {
            try { Launch(run); } catch { attempted[task.Id] = task.Cycle(DateTime.Now); if (active?.Task.Id == task.Id) active = null; throw; }
            store.Log("手动启动，等待进程：" + task.Name);
        }
        if (active?.Task.Id == task.Id) active = null;
        attempted.Remove(task.Id); manual[task.Id] = run;
    }
    public void Retry(LaunchTask task) { store.Clear(task.Id); attempted.Remove(task.Id); if (active?.Task.Id == task.Id && delay) active = null; }
    public void Reset()
    {
        store.Clear(); attempted.Clear(); foreach (var run in manual.Values) Suppress(run);
        if (active is not null) { if (delay) active = null; else Suppress(active); }
        SetPause(true, Managing); store.Log("全部重置，自动队列已暂停；已发起的任务继续监测。");
    }
    public void Changed()
    {
        if (active is not null) { if (delay) active = null; else if (Current(active) is null) { attempted.Remove(active.Task.Id); detached.Add(active); active = null; } }
        foreach (var pair in manual.ToArray()) if (Current(pair.Value) is null) { attempted.Remove(pair.Key); detached.Add(pair.Value); manual.Remove(pair.Key); }
        foreach (var id in attempted.Keys.Where(id => store.Tasks.All(t => t.Id != id)).ToArray()) attempted.Remove(id);
    }
    public void Skip(LaunchTask task) { attempted[task.Id] = task.Cycle(DateTime.Now); if (active?.Task.Id == task.Id && delay) active = null; store.Log("本周期跳过：" + task.Name); }
    public void Mark(LaunchTask task) { store.Mark(task, DateTime.Now); if (active?.Task.Id == task.Id && delay) active = null; store.Log("手动标记完成：" + task.Name); }
    public void Clear(LaunchTask task)
    {
        store.Clear(task.Id); attempted.Remove(task.Id); if (manual.TryGetValue(task.Id, out var run)) Suppress(run);
        if (active?.Task.Id == task.Id) { if (delay) active = null; else Suppress(active); }
        SetPause(true, Managing); store.Log("已清除完成并暂停队列：" + task.Name);
    }
    public bool AllDone => !Launching && active is null && manual.Count == 0 && detached.Count == 0 && store.Error is null && store.Tasks.Any(t => t.Enabled) && store.Tasks.All(t => !t.Enabled || store.Done(t, DateTime.Now));
    public string Status(LaunchTask task)
    {
        bool done = store.Done(task, DateTime.Now); var run = manual.GetValueOrDefault(task.Id) ?? (active?.Task.Id == task.Id && !delay ? active : null);
        if (run is not null) return run.Running ? (done ? "运行中 · 已完成" : "运行中 · 未完成") : "等待进程";
        if (!task.Enabled) return "自动启动已停用";
        if (done) return "本周期已完成";
        if (active?.Task.Id == task.Id && delay) return paused ? "等待恢复" : $"{Math.Max(0, Math.Ceiling(active.Deadline - Seconds))} 秒后启动";
        return attempted.GetValueOrDefault(task.Id) == task.Cycle(DateTime.Now) ? "本周期已跳过 · 可重试" : "尚未启动";
    }
}
