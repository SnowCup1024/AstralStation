using Microsoft.UI.Dispatching;

namespace AstralStation.Modules.Launch;

public sealed class LaunchSession : IDisposable
{
    public LaunchStore Store { get; }
    public WindowsProcessHost Processes { get; } = new();
    public Scheduler Scheduler { get; }
    private readonly DispatcherQueueTimer timer;
    private bool ticking, disposed;
    private int interactions;
    private int generation;
    public bool Managing { get; private set; }
    public bool Interacting => interactions > 0;
    public CancellationTokenSource? DiscoveryCancellation { get; set; }
    public event Action? Changed;
    public event Action? TasksChanged;
    public event Action<IReadOnlyList<Scheduler.RunEvent>>? RunsChanged;
    public LaunchSession(string root, bool paused, DispatcherQueue queue)
    {
        Store = new(root); Scheduler = new(Store, Processes); Scheduler.SetPause(paused, false);
        timer = queue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(500); timer.Tick += Tick;
    }
    public void Start() { timer.Start(); Notify(); }
    public void SetManaging(bool value) { Managing = value; PauseChanged(); }
    public IDisposable Interaction()
    {
        interactions++; PauseChanged(); return new Scope(() => { interactions--; PauseChanged(); });
    }
    private sealed class Scope(Action end) : IDisposable { private Action? end = end; public void Dispose() { var action = end; end = null; action?.Invoke(); } }
    private void PauseChanged() { Scheduler.SetPause(Scheduler.UserPaused, Managing || Interacting); Notify(); }
    public void TogglePause() { Scheduler.SetPause(!Scheduler.UserPaused, Managing || Interacting); Notify(); }
    public void SaveTasks(IEnumerable<LaunchTask> next)
    {
        if (Scheduler.Launching) throw new InvalidOperationException("正在发起启动，请稍后操作。");
        Store.SaveTasks(next); Scheduler.Changed(); generation++; TasksChanged?.Invoke(); Notify();
    }
    public void Execute(Action action) { if (Scheduler.Launching) throw new InvalidOperationException("正在启动程序。"); try { action(); } finally { generation++; Notify(); } }
    public void Notify() { foreach (var task in Store.Tasks) { task.Status = Scheduler.Status(task); task.Completion = $"今日{(Store.Done(task, DateTime.Now) ? "已" : "未")}完成（{task.ResetLabel}重置）"; } Changed?.Invoke(); }
    private async void Tick(DispatcherQueueTimer sender, object args)
    {
        if (ticking || disposed || Scheduler.Launching) return;
        ticking = true;
        try
        {
            if (Store.Error is null)
            {
                int before = generation;
                var names = await Task.Run(Processes.Names);
                if (disposed) return;
                if (before == generation) Scheduler.Tick(names);
            }
            RunsChanged?.Invoke(Scheduler.TakeEvents());
        }
        catch (Exception ex) { Store.Fail("调度已停止：" + ex.Message); Scheduler.SetPause(true, Managing || Interacting); }
        finally { ticking = false; if (!disposed) Notify(); }
    }
    public void Dispose() { disposed = true; timer.Stop(); timer.Tick -= Tick; }
}
