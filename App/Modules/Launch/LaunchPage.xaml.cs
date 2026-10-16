using AstralStation.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;
using Windows.UI.Core;

namespace AstralStation.Modules.Launch;

public sealed partial class LaunchPage : UserControl
{
    private readonly LaunchSession session;
    private readonly Dialogs dialogs;
    private readonly DesktopHost desktop;
    private readonly ObservableCollection<LaunchTask> library = [];
    private readonly HistoryView history;
    private readonly Dictionary<string, Task<BitmapImage?>> icons = new(StringComparer.OrdinalIgnoreCase);
    private bool ready, commandBusy;
    private IDisposable? reorderScope;
    public bool IsQueue => Tabs.SelectedIndex == 0;
    public bool IsLibrary => Tabs.SelectedIndex == 1;
    public event Action? SectionChanged;
    public LaunchPage(LaunchSession session, Dialogs dialogs, DesktopHost desktop)
    {
        this.session = session; this.dialogs = dialogs; this.desktop = desktop;
        InitializeComponent(); history = new(session, dialogs); CalendarHost.Content = history;
        LibraryList.ItemsSource = library;
        QueueList.PointerPressed += ClearEmptySelection; LibraryList.PointerPressed += ClearEmptySelection;
        session.Changed += Refresh; session.TasksChanged += Reload;
        AutoClose.IsOn = session.Store.Settings.AutoClose; Tray.IsOn = session.Store.Settings.Tray; CloseToTray.IsOn = session.Store.Settings.CloseToTray; CloseToTray.IsEnabled = Tray.IsOn;
        try { Login.IsOn = desktop.LoginEnabled; } catch (Exception ex) { ErrorBar.Message = ex.Message; ErrorBar.IsOpen = true; }
        DataPath.Text = session.Store.Root; ready = true; Reload();
    }
    private void Reload()
    {
        string? selected = (LibraryList.SelectedItem as LaunchTask)?.Id, queue = (QueueList.SelectedItem as LaunchTask)?.Id;
        library.Clear(); foreach (var task in session.Store.Tasks) library.Add(task);
        QueueList.ItemsSource = session.Store.Tasks;
        LibraryList.SelectedItem = library.FirstOrDefault(t => t.Id == selected); QueueList.SelectedItem = session.Store.Tasks.FirstOrDefault(t => t.Id == queue);
        var paths = session.Store.Tasks.Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in icons.Keys.Where(k => !paths.Contains(k)).ToArray()) icons.Remove(key);
        Refresh();
    }
    private void Refresh()
    {
        if (!ready) return;
        int total = session.Store.Tasks.Count(t => t.Enabled), done = session.Store.Tasks.Count(t => t.Enabled && session.Store.Done(t, DateTime.Now));
        Summary.Text = $"{done} / {total}  本周期已完成"; Progress.Value = total == 0 ? 0 : done * 100.0 / total;
        PauseButton.Content = session.Scheduler.UserPaused ? "恢复自动队列" : "暂停自动队列";
        QueueHint.Text = session.Scheduler.UserPaused ? "自动队列已暂停，已启动的任务继续监测。" : "按任务库顺序启动，等待当前程序退出后继续。";
        EmptyQueue.Visibility = total == 0 && session.Store.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorBar.IsOpen = session.Store.Error is not null; ErrorBar.Message = session.Store.Error ?? "";
        ActivityText.Text = string.Join("\n", session.Store.Activities.Where(a => a.Time >= DateTime.Now.AddDays(-1)).Select(a => a.ToString()));
        RunButton.IsEnabled = RetryButton.IsEnabled = QueueList.SelectedItem is not null && session.Store.Error is null;
        QueueActions.Visibility = QueueList.SelectedItem is LaunchTask ? Visibility.Visible : Visibility.Collapsed;
        LibraryActions.Visibility = LibraryList.SelectedItem is LaunchTask ? Visibility.Visible : Visibility.Collapsed;
        if (LibraryList.SelectedItem is LaunchTask selectedTask)
        {
            ToggleTaskIcon.Glyph = selectedTask.Enabled ? "\uE769" : "\uE768";
            ToolTipService.SetToolTip(ToggleTaskButton, selectedTask.Enabled ? "停用自动启动" : "启用自动启动");
        }
        if (Tabs.SelectedIndex == 2) history.RefreshIfNeeded();
    }
    private void TabChanged(object sender, SelectionChangedEventArgs e) { if (!ready || !ReferenceEquals(e.OriginalSource, Tabs)) return; session.SetManaging(Tabs.SelectedIndex == 1); if (Tabs.SelectedIndex == 2) history.RefreshIfNeeded(true); SectionChanged?.Invoke(); }
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
        var edited = await TaskEditor.Edit(task, dialogs, session, options); if (edited is null) return;
        var tasks = session.Store.Tasks.Select(t => t.Clone()).ToList(); int index = tasks.FindIndex(t => t.Id == id);
        if (id is null) tasks.Add(edited); else if (index >= 0) tasks[index] = edited; else return;
        session.SaveTasks(tasks);
    }
    private async Task Action(string id, string action)
    {
        var task = Current(id); if (task is null) return;
        if (action is "edit" or "options") { await Edit(id, action == "options"); return; }
        if (action is "run" or "retry")
        {
            if (!await dialogs.Confirm(action == "retry" ? "重试任务" : "启动任务", task.Name + "\n" + task.Path)) return;
            task = Current(id); if (task is null) return;
            if (action == "retry") session.Execute(() => session.Scheduler.Retry(task));
            session.Execute(() => session.Scheduler.RunNow(task)); return;
        }
        if (action is "mark" or "clear" or "delete")
        {
            if (!await dialogs.Confirm(action == "delete" ? "删除任务" : action == "mark" ? "标记完成" : "清除完成状态", task.Name + (action == "delete" ? "\n已有程序和历史记录会保留。" : "\n手动修改不会生成启动历史。"))) return;
            task = Current(id); if (task is null) return;
        }
        if (action == "copy") { Dialogs.Copy(task.Path); return; }
        if (action == "folder") { DesktopHost.OpenFolder(task.Path); return; }
        if (action == "skip") { session.Execute(() => session.Scheduler.Skip(task)); return; }
        if (action == "mark") { session.Execute(() => session.Scheduler.Mark(task)); return; }
        if (action == "clear") { session.Execute(() => session.Scheduler.Clear(task)); return; }
        var tasks = session.Store.Tasks.Select(t => t.Clone()).ToList(); int index = tasks.FindIndex(t => t.Id == id);
        if (action == "delete") tasks.RemoveAt(index);
        else if (action == "duplicate") { var copy = task.Clone(); copy.Id = Guid.NewGuid().ToString(); copy.Name += " · 副本"; copy.Enabled = false; tasks.Insert(index + 1, copy); }
        else if (action == "toggle") tasks[index].Enabled = !tasks[index].Enabled;
        else if (action is "up" or "down") { int target = index + (action == "up" ? -1 : 1); if (target < 0 || target >= tasks.Count) return; (tasks[index], tasks[target]) = (tasks[target], tasks[index]); }
        session.SaveTasks(tasks);
    }
    private Task SelectedAction(string action) { string? id = Selected?.Id; return id is null ? Task.CompletedTask : Guard(() => Action(id, action)); }
    private async void AddTask(object sender, RoutedEventArgs e) => await Guard(() => Edit(null));
    private void Pause(object sender, RoutedEventArgs e) => session.TogglePause();
    private async void RunSelected(object sender, RoutedEventArgs e) => await SelectedAction("run");
    private async void RetrySelected(object sender, RoutedEventArgs e) => await SelectedAction("retry");
    private async void MarkSelected(object sender, RoutedEventArgs e) => await SelectedAction("mark");
    private async void EditSelected(object sender, RoutedEventArgs e) => await SelectedAction("edit");
    private async void OptionsSelected(object sender, RoutedEventArgs e) => await SelectedAction("options");
    private async void DuplicateSelected(object sender, RoutedEventArgs e) => await SelectedAction("duplicate");
    private async void ToggleSelected(object sender, RoutedEventArgs e) => await SelectedAction("toggle");
    private async void DeleteSelected(object sender, RoutedEventArgs e) => await SelectedAction("delete");
    private async void MoveUp(object sender, RoutedEventArgs e) => await SelectedAction("up");
    private async void MoveDown(object sender, RoutedEventArgs e) => await SelectedAction("down");
    private async void DoubleRun(object sender, DoubleTappedRoutedEventArgs e) => await SelectedAction("run");
    private async void DoubleEdit(object sender, DoubleTappedRoutedEventArgs e) => await SelectedAction("edit");
    private async void RowRun(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string id }) await Guard(() => Action(id, "run")); }
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
        foreach (var (label, action) in new[] { ("启动", "run"), ("重试", "retry"), ("编辑", "edit"), ("启动选项", "options"), ("复制任务", "duplicate"), ("启用 / 停用", "toggle"), ("标记完成", "mark"), ("清除完成", "clear"), ("跳过本周期", "skip"), ("打开所在目录", "folder"), ("复制路径", "copy"), ("删除", "delete") })
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
    private async void DetectGames(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        using var cancellation = new CancellationTokenSource(); var label = new TextBlock { Text = "正在检测常用目录、注册表和快捷方式…", TextWrapping = TextWrapping.Wrap }; var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 350, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Border Background="{ThemeResource CardBackgroundFillColorDefaultBrush}" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1" CornerRadius="8" Padding="12" Margin="0,4">
                <StackPanel Spacing="6"><TextBlock Text="{Binding Name}" FontWeight="SemiBold"/><TextBlock Text="{Binding Path}" FontSize="12" TextWrapping="Wrap" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/></StackPanel>
              </Border>
            </DataTemplate>
            """);
        var panel = new StackPanel { Spacing = 16, MinWidth = 440, MaxWidth = 560 }; panel.Children.Add(label); panel.Children.Add(list);
        var dialog = new ContentDialog { Title = "检测已安装游戏", Content = panel, PrimaryButtonText = "添加所选", CloseButtonText = "取消", IsPrimaryButtonEnabled = false };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count > 0;
        Task? work = null;
        dialog.Opened += (_, _) => { work = Detect(); };
        async Task Detect()
        {
            try { var tasks = await Task.Run(() => Recommendations.Detect(cancellation.Token)); if (cancellation.IsCancellationRequested) return; list.ItemsSource = tasks.Where(t => !session.Store.Tasks.Any(old => string.Equals(System.IO.Path.GetFullPath(old.Path), t.Path, StringComparison.OrdinalIgnoreCase))).ToList(); label.Text = list.Items.Count == 0 ? "没有找到可添加的游戏。可使用添加任务手动选择路径。" : "选择要添加的游戏。默认每日 04:00 重置，可在任务库中修改。"; dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count > 0; }
            catch (OperationCanceledException) { } catch (Exception ex) { label.Text = ex.Message; }
        }
        dialog.Closing += async (_, args) => { cancellation.Cancel(); if (work is not null) { var deferral = args.GetDeferral(); await work; deferral.Complete(); } };
        if (await dialogs.Show(dialog) == ContentDialogResult.Primary && list.SelectedItems.Count > 0) session.SaveTasks(session.Store.Tasks.Concat(list.SelectedItems.Cast<LaunchTask>()));
    });
    private async void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        await Guard(() =>
        {
            var old = session.Store.Settings; var next = old.Clone(); next.AutoClose = AutoClose.IsOn; next.Tray = Tray.IsOn; next.CloseToTray = next.Tray && CloseToTray.IsOn;
            try { desktop.SetTray(next.Tray); session.Store.SaveSettings(next); }
            catch { desktop.SetTray(old.Tray); throw; }
            finally { ready = false; AutoClose.IsOn = session.Store.Settings.AutoClose; Tray.IsOn = session.Store.Settings.Tray; CloseToTray.IsOn = session.Store.Settings.CloseToTray; CloseToTray.IsEnabled = Tray.IsOn; ready = true; }
            return Task.CompletedTask;
        });
    }
    private async void LoginChanged(object sender, RoutedEventArgs e) { if (ready) await Guard(() => { try { desktop.SetLogin(Login.IsOn); } finally { ready = false; Login.IsOn = desktop.LoginEnabled; ready = true; } return Task.CompletedTask; }); }
    private async void OpenData(object sender, RoutedEventArgs e) => await Guard(() => { DesktopHost.OpenFolder(session.Store.Root); return Task.CompletedTask; });
    private async void IconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not LaunchTask task) return;
        string path = task.Path;
        if (!icons.TryGetValue(path, out var result)) icons[path] = result = LoadIcon(path);
        var icon = await result; if (image.DataContext is LaunchTask current && current.Path == path) image.Source = icon;
    }
    internal static async Task<BitmapImage?> LoadIcon(string path)
    {
        try { var file = await StorageFile.GetFileFromPathAsync(System.IO.Path.GetFullPath(path)); using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32, ThumbnailOptions.UseCurrentScale); if (thumb is null) return null; var image = new BitmapImage(); await image.SetSourceAsync(thumb); return image; } catch { return null; }
    }
}
