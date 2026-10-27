using AstralStation.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace AstralStation.Launch;

public sealed partial class LaunchPage : UserControl
{
    private readonly LaunchSession session;
    private readonly Dialogs dialogs;
    private readonly ObservableCollection<LaunchTask> library = [];
    private bool ready, commandBusy;
    private IDisposable? reorderScope;
    public bool IsLibrary => Tabs.SelectedIndex == 1;
    public LaunchPage(LaunchSession session, Dialogs dialogs)
    {
        this.session = session; this.dialogs = dialogs;
        InitializeComponent();
        RingLabel.Foreground = RingValue.Foreground = RingTask.Foreground = Progress.ProgressBrush;
        LibraryList.ItemsSource = library;
        QueueList.PointerPressed += ClearEmptySelection; LibraryList.PointerPressed += ClearEmptySelection;
        session.Changed += Refresh; session.TasksChanged += Reload;
        ready = true; Reload();
    }
    private void Reload()
    {
        string? selected = (LibraryList.SelectedItem as LaunchTask)?.Id, queue = (QueueList.SelectedItem as LaunchTask)?.Id;
        for (int i = 0; i < session.Store.Tasks.Count; i++) session.Store.Tasks[i].Order = i + 1;
        library.Clear(); foreach (var task in session.Store.Tasks) library.Add(task);
        QueueList.ItemsSource = session.Store.Tasks;
        LibraryList.SelectedItem = library.FirstOrDefault(t => t.Id == selected); QueueList.SelectedItem = session.Store.Tasks.FirstOrDefault(t => t.Id == queue);
        Refresh();
    }
    private void Refresh()
    {
        if (!ready) return;
        int total = session.Store.Tasks.Count, done = session.Store.Tasks.Count(t => session.Store.Done(t, DateTime.Now));
        Summary.Text = $"{done} / {total}  今日已完成"; Progress.SetProgress(total == 0 ? 0 : (double)done / total);
        var display = session.Scheduler.QueueDisplay;
        RingLabel.Text = display.Label; RingValue.Text = display.Value; RingTask.Text = display.Name;
        PauseButton.Content = session.Scheduler.UserPaused ? "恢复自动队列" : "暂停自动队列";
        EmptyQueue.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorBar.IsOpen = session.Store.Error is not null; ErrorBar.Message = session.Store.Error ?? "";
        RunButton.IsEnabled = QueueList.SelectedItem is not null && session.Store.Error is null;
        bool selectedDone = QueueList.SelectedItem is LaunchTask selected && session.Store.Done(selected, DateTime.Now);
        RunButton.Content = selectedDone ? "重试此任务" : "启动此任务";
        MarkButton.Visibility = selectedDone ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.Visibility = selectedDone ? Visibility.Visible : Visibility.Collapsed;
        QueueActions.Visibility = QueueList.SelectedItem is LaunchTask ? Visibility.Visible : Visibility.Collapsed;
        LibraryActions.Visibility = LibraryList.SelectedItem is LaunchTask ? Visibility.Visible : Visibility.Collapsed;
    }
    private void QueueSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Lists have their own viewport; selecting a task cannot scroll the summary out of view.
        double columnWidth = e.NewSize.Width < 700 ? 204 : 252;
        double diameter = Math.Clamp(Math.Min(e.NewSize.Height - 100, columnWidth - 16), 144, 200);
        RingViewport.Width = RingViewport.Height = diameter;
        RingPanel.Spacing = e.NewSize.Height < 320 ? 8 : 16;
        QueueLayout.ColumnDefinitions[0].Width = new GridLength(columnWidth);
        QueueLayout.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        QueueLayout.RowDefinitions[1].Height = new GridLength(0);
    }
    private void TabChanged(object sender, SelectionChangedEventArgs e) { if (!ready || !ReferenceEquals(e.OriginalSource, Tabs)) return; session.SetManaging(Tabs.SelectedIndex == 1); }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => Refresh();
    private static void ClearEmptySelection(object sender, PointerRoutedEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && !ReferenceEquals(element, sender)) { if (element is ListViewItem) return; element = VisualTreeHelper.GetParent(element); }
        if (sender is ListView list) list.SelectedItem = null;
    }
    private async Task Guard(Func<Task> action)
    {
        if (commandBusy || dialogs.Busy || session.Scheduler.Launching) return;
        commandBusy = true; using var scope = session.Interaction();
        try { await action(); } catch (Exception ex) { await dialogs.Error(ex.Message); } finally { commandBusy = false; session.Notify(); }
    }
    private LaunchTask? Selected => (Tabs.SelectedIndex == 1 ? LibraryList.SelectedItem : QueueList.SelectedItem) as LaunchTask;
    private LaunchTask? Current(string id) => session.Store.Tasks.FirstOrDefault(t => t.Id == id);
    private async Task Edit(string? id, bool options = false, string? path = null, LaunchTask? preset = null)
    {
        var task = id is null ? preset ?? new LaunchTask { Path = path ?? "", Name = path is null ? "" : System.IO.Path.GetFileNameWithoutExtension(path), ProcessNames = path?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true ? [System.IO.Path.GetFileNameWithoutExtension(path)] : [] } : Current(id);
        if (task is null) return;
        var edited = await TaskEditor.Edit(task, dialogs, session, options, id is null); if (edited is null) return;
        var tasks = new List<LaunchTask>(session.Store.Tasks); int index = tasks.FindIndex(t => t.Id == id);
        if (id is null) tasks.AddRange(edited); else if (index >= 0) tasks[index] = edited[0]; else return;
        session.SaveTasks(tasks);
    }
    private async Task Action(string id, string action)
    {
        var task = Current(id); if (task is null) return;
        if (action is "edit" or "options") { await Edit(id, action == "options"); return; }
        if (action == "run")
        {
            if (!await dialogs.Confirm(session.Store.Done(task, DateTime.Now) ? "重试任务" : "启动任务", task.Name + "\n" + task.Path)) return;
            task = Current(id); if (task is null) return;
            if (session.Store.Done(task, DateTime.Now)) session.Execute(() => session.Scheduler.Retry(task));
            session.Execute(() => session.Scheduler.RunNow(task)); return;
        }
        if (action is "mark" or "clear" or "delete")
        {
            if (!await dialogs.Confirm(action == "delete" ? "删除任务" : action == "mark" ? "标记完成" : "重置此任务进度", task.Name + (action == "delete" ? "\n已有程序和历史记录会保留。" : action == "clear" ? "\n清除此任务的今日完成状态并暂停自动队列，已发起的运行继续监测。其他任务进度保留。" : "\n手动修改不会生成启动历史。"))) return;
            task = Current(id); if (task is null) return;
        }
        if (action == "copy") { Dialogs.Copy(task.Path); return; }
        if (action == "folder") { DesktopHost.OpenFolder(task.Path); return; }
        if (action == "skip") { session.Execute(() => session.Scheduler.Skip(task)); return; }
        if (action == "mark") { session.Execute(() => session.Scheduler.Mark(task)); return; }
        if (action == "clear") { session.Execute(() => session.Scheduler.Clear(task)); return; }
        var tasks = new List<LaunchTask>(session.Store.Tasks); int index = tasks.FindIndex(t => t.Id == id);
        if (action == "delete") tasks.RemoveAt(index);
        else if (action == "duplicate") { var copy = task.Clone(); copy.Id = Guid.NewGuid().ToString(); copy.Name += " · 副本"; tasks.Insert(index + 1, copy); }
        else if (action is "up" or "down") { int target = index + (action == "up" ? -1 : 1); if (target < 0 || target >= tasks.Count) return; (tasks[index], tasks[target]) = (tasks[target], tasks[index]); }
        session.SaveTasks(tasks);
    }
    private Task SelectedAction(string action) { string? id = Selected?.Id; return id is null ? Task.CompletedTask : Guard(() => Action(id, action)); }
    private async void AddTask(object sender, RoutedEventArgs e) => await Guard(() => Edit(null));
    private void Pause(object sender, RoutedEventArgs e) => session.TogglePause();
    private async void RunSelected(object sender, RoutedEventArgs e) => await SelectedAction("run");
    private async void ClearSelected(object sender, RoutedEventArgs e) => await SelectedAction("clear");
    private async void MarkSelected(object sender, RoutedEventArgs e) => await SelectedAction("mark");
    private async void EditSelected(object sender, RoutedEventArgs e) => await SelectedAction("edit");
    private async void OptionsSelected(object sender, RoutedEventArgs e) => await SelectedAction("options");
    private async void DuplicateSelected(object sender, RoutedEventArgs e) => await SelectedAction("duplicate");
    private async void DeleteSelected(object sender, RoutedEventArgs e) => await SelectedAction("delete");
    private async void MoveUp(object sender, RoutedEventArgs e) => await SelectedAction("up");
    private async void MoveDown(object sender, RoutedEventArgs e) => await SelectedAction("down");
    private async void DoubleRun(object sender, DoubleTappedRoutedEventArgs e) => await SelectedAction("run");
    private async void DoubleEdit(object sender, DoubleTappedRoutedEventArgs e) => await SelectedAction("edit");
    private async void ResetAll(object sender, RoutedEventArgs e) => await Guard(async () => { if (await dialogs.Confirm("重置全部进度", "清除完成状态并暂停队列，已发起的运行继续监测。")) session.Execute(session.Scheduler.Reset); });
    internal static bool Ctrl => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
    internal static bool Shift => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
    private async void TaskKey(object sender, KeyRoutedEventArgs e)
    {
        string? action = e.Key switch { VirtualKey.Enter => Tabs.SelectedIndex == 1 ? "edit" : "run", VirtualKey.F2 => "edit", VirtualKey.Delete => "delete", VirtualKey.E when Ctrl => "options", VirtualKey.D when Ctrl => "duplicate", VirtualKey.C when Ctrl => "copy", VirtualKey.Up when Ctrl => "up", VirtualKey.Down when Ctrl => "down", _ => null };
        if (e.Key == VirtualKey.Escape) { ((ListView)sender).SelectedItem = null; e.Handled = true; }
        if (e.Key == VirtualKey.Application || (Shift && e.Key == VirtualKey.F10)) { ShowMenu((ListView)sender, Selected); e.Handled = true; }
        if (action is not null) { e.Handled = true; await SelectedAction(action); }
    }
    private void TaskMenu(object sender, RightTappedRoutedEventArgs e)
    {
        var element = e.OriginalSource as FrameworkElement; while (element is not null && element.DataContext is not LaunchTask) element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        if (element?.DataContext is LaunchTask task) { ((ListView)sender).SelectedItem = task; ShowMenu(element, task); e.Handled = true; }
    }
    private void ShowMenu(FrameworkElement target, LaunchTask? task)
    {
        if (task is null) return; string id = task.Id; var menu = new MenuFlyout(); IDisposable? scope = null;
        bool done = session.Store.Done(task, DateTime.Now);
        foreach (var (label, action) in new[] { (done ? "重试" : "启动", "run"), ("编辑", "edit"), ("启动选项", "options"), ("复制任务", "duplicate"), done ? ("重置此任务进度", "clear") : ("标注此任务为完成", "mark"), ("跳过本周期", "skip"), ("打开所在目录", "folder"), ("复制路径", "copy"), ("删除", "delete") })
        {
            var item = new MenuFlyoutItem { Text = label }; item.Click += (_, _) => { menu.Hide(); DispatcherQueue.TryEnqueue(async () => await Guard(() => Action(id, action))); }; menu.Items.Add(item);
        }
        menu.Opened += (_, _) => scope = session.Interaction(); menu.Closed += (_, _) => scope?.Dispose(); menu.ShowAt(target);
    }
    private void ReorderStarting(object sender, DragItemsStartingEventArgs e) { if (commandBusy) { e.Cancel = true; return; } reorderScope = session.Interaction(); }
    private async void Reordered(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        try { if (!library.Select(t => t.Id).SequenceEqual(session.Store.Tasks.Select(t => t.Id))) session.SaveTasks(library.ToArray()); }
        catch (Exception ex) { Reload(); await dialogs.Error(ex.Message); } finally { reorderScope?.Dispose(); reorderScope = null; }
    }
    private void FilesOver(object sender, DragEventArgs e) { if (e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.Copy; e.Handled = true; } }
    private async void FilesDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return; e.Handled = true; var deferral = e.GetDeferral();
        try { var files = await e.DataView.GetStorageItemsAsync(); await Guard(async () => { foreach (var file in files.OfType<StorageFile>().Where(f => new[] { ".exe", ".lnk", ".bat", ".cmd", ".ps1" }.Contains(f.FileType.ToLowerInvariant())).Take(16)) await Edit(null, path: file.Path); }); }
        catch (Exception ex) { await dialogs.Error(ex.Message); } finally { deferral.Complete(); }
    }
}
