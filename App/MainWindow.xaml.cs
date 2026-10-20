using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using AstralStation.Services;
using AstralStation.Launch;

namespace AstralStation;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Func<FrameworkElement>> routes = new();
    private readonly Dictionary<string, FrameworkElement> pages = new();
    private readonly LaunchSession session;
    private readonly DesktopHost desktop;
    private readonly Dialogs dialogs;
    private LaunchPage? launchPage;
    private string activeRoute = "launch";
    private bool forceExit, closed, keepOpen, restoreAfterRuns;
    private double? closeDeadline;
    private ContentDialog? completionDialog;
    private TextBlock? countdownText;
    private IDisposable? trayScope;
    private readonly Dictionary<long, FocusRequest> running = [];
    private sealed class FocusRequest(LaunchTask task) { public LaunchTask Task = task; public bool Minimized, Attempted; public double Deadline; }
    public MainWindow(string dataRoot, bool paused, bool background)
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        session = new(dataRoot, paused, DispatcherQueue);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        double scale = DesktopHost.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min(work.Width, (int)(1040 * scale));
        int height = Math.Min(work.Height, (int)(760 * scale));
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        desktop = new(WinRT.Interop.WindowNative.GetWindowHandle(this), dataRoot);
        dialogs = new(this, Root, session);
        Root.RequestedTheme = session.Store.Settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        try { desktop.SetTray(session.Store.Settings.Tray); } catch (Exception ex) { session.Store.Fail(ex.Message); }
        routes["launch"] = () => { launchPage = new(session, dialogs, desktop); launchPage.SectionChanged += () => { CancelCountdown(); }; return launchPage; };
        routes["steam"] = () =>
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "--steam-root");
            string? testSteam = at >= 0 && at + 1 < args.Length && args.Contains("--data-dir") ? args[at + 1] : null;
            try { return new SteamLaunchPage(dialogs, testSteam is null ? null : Path.Combine(dataRoot, "SteamLaunch", SteamLaunch.AppId), testSteam); }
            catch (Exception ex) { return Intro("Steam 启动国服", "无法读取配置", ex.Message); }
        };
        routes["settings"] = Settings;
        routes["about"] = () => Intro("关于星间驿站", "Alpha 3.3", "属于自己的游戏工具集合。\n\nC# · WinUI\n仅提供简体中文界面。");
        Navigation.SelectedItem = Navigation.MenuItems[0];
        session.RunsChanged += events => { foreach (var item in events) { if (item.Started) { if (item.LaunchedHere) running[item.Serial] = new(item.Task); CancelCountdown(); } else running.Remove(item.Serial); } };
        session.Changed += UpdateLifecycle;
        desktop.RestoreRequested += Restore;
        desktop.UserRestored += () => { foreach (var request in running.Values) request.Attempted = true; };
        desktop.PauseRequested += session.TogglePause;
        desktop.ExitRequested += RequestExit;
        desktop.UpgradeRequested += () =>
        {
            if (session.Interacting || session.Scheduler.Launching || session.Store.Error is not null) { Restore(); return; }
            try { session.Scheduler.SaveHandoff(); RequestExit(); }
            catch (Exception ex) { session.Store.Fail("版本交接失败：" + ex.Message); Restore(); }
        };
        desktop.MenuActive += active => { if (active) trayScope = session.Interaction(); else { trayScope?.Dispose(); trayScope = null; } };
        AppWindow.Closing += (_, args) =>
        {
            if (forceExit) return;
            if (session.Interacting || session.Scheduler.Launching) { args.Cancel = true; session.DiscoveryCancellation?.Cancel(); return; }
            if (session.Store.Settings.Tray && session.Store.Settings.CloseToTray) { args.Cancel = true; desktop.Minimize(); }
        };
        Closed += (_, _) => { closed = true; completionDialog?.Hide(); session.Dispose(); desktop.Dispose(); };
        Root.Loaded += (_, _) => { session.Start(); if (background && session.Store.Settings.Tray) desktop.Minimize(); };
    }
    private void NavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: string key } && pages.TryGetValue(key, out var page)) ResetPage(page);
    }
    private static void ResetPage(FrameworkElement page)
    {
        if (page is LaunchPage launch) launch.ShowQueue();
        else if (page is SteamLaunchPage steam) steam.ShowGames();
    }
    private void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string key } || !routes.TryGetValue(key, out var factory)) return;
        activeRoute = key;
        if (!pages.TryGetValue(key, out var page)) pages[key] = page = factory();
        ResetPage(page);
        PageHost.Content = page;
        PageHost.ContentTransitions = new TransitionCollection { new EntranceThemeTransition() };
        if (session is not null) { session.SetManaging(key == "launch" && launchPage?.IsLibrary == true); CancelCountdown(); }
    }
    private static StackPanel Intro(string title, string subtitle, string body)
    {
        var panel = new StackPanel { Margin = new Thickness(28), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["PageTitleStyle"] });
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = subtitle, Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        if (!string.IsNullOrEmpty(body)) content.Children.Add(new TextBlock { Text = body });
        panel.Children.Add(new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = content });
        return panel;
    }
    private FrameworkElement Settings()
    {
        var panel = Intro("设置", "外观", "");
        var theme = new ComboBox { Header = "应用主题", ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = (int)Root.RequestedTheme, MinWidth = 240 };
        theme.SelectionChanged += async (_, _) => { try { var next = session.Store.Settings.Clone(); next.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "Default" }; session.Store.SaveSettings(next); Root.RequestedTheme = (ElementTheme)theme.SelectedIndex; } catch (Exception ex) { await dialogs.Error(ex.Message); } };
        ((StackPanel)((Border)panel.Children[1]).Child).Children.Add(theme);
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private void Restore() { foreach (var request in running.Values) request.Attempted = true; if (closeDeadline is not null) closeDeadline = Scheduler.Seconds + 15; desktop.Restore(); }
    private void RequestExit() { if (session.Interacting || session.Scheduler.Launching) { Restore(); return; } forceExit = true; completionDialog?.Hide(); Close(); }
    private void CancelCountdown() { closeDeadline = null; completionDialog?.Hide(); }
    private bool CanClose => session.Store.Settings.AutoClose && !keepOpen && activeRoute == "launch" && launchPage?.IsQueue == true && session.Scheduler.AllDone && !session.Interacting && session.Store.Error is null;
    private void UpdateLifecycle()
    {
        if (closed) return;
        if (session.Interacting || session.Store.Error is not null) { CancelCountdown(); return; }
        if (running.Count == 0 && restoreAfterRuns && !CanClose) { restoreAfterRuns = false; desktop.Restore(); }
        foreach (var request in running.Values)
        {
            if (!request.Minimized) { request.Minimized = true; request.Deadline = Scheduler.Seconds + 15; restoreAfterRuns = true; desktop.Minimize(); }
            if (!request.Attempted)
            {
                if (Scheduler.Seconds >= request.Deadline) request.Attempted = true;
                else try { if (DesktopHost.Focus(request.Task, session.Processes)) request.Attempted = true; } catch { request.Attempted = true; }
            }
        }
        if (!CanClose) { CancelCountdown(); return; }
        closeDeadline ??= Scheduler.Seconds + 15;
        int remaining = Math.Max(0, (int)Math.Ceiling(closeDeadline.Value - Scheduler.Seconds));
        if (countdownText is not null) countdownText.Text = $"所有任务已完成，运行记录已保存。\n{remaining} 秒后退出星间驿站。";
        if (remaining == 0) { RequestExit(); return; }
        if (!desktop.IsHidden && completionDialog is null && Root.XamlRoot is not null) ShowCountdown(remaining);
    }
    private async void ShowCountdown(int remaining)
    {
        countdownText = new TextBlock { Text = $"所有任务已完成，运行记录已保存。\n{remaining} 秒后退出星间驿站。", TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(countdownText);
        var keepButton = new Button { Content = "保持打开（仅本次）", HorizontalAlignment = HorizontalAlignment.Right };
        ToolTipService.SetToolTip(keepButton, "仅本次保持打开");
        body.Children.Add(keepButton);
        var dialog = new ContentDialog { Title = "今日安排已完成", Content = body, XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme };
        keepButton.Click += (_, _) => { keepOpen = true; closeDeadline = null; dialog.Hide(); };
        completionDialog = dialog;
        dialog.CloseButtonClick += (_, _) => { keepOpen = true; closeDeadline = null; };
        dialog.KeyDown += (_, e) => { if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Escape) { keepOpen = true; closeDeadline = null; dialog.Hide(); e.Handled = true; } };
        try { await dialog.ShowAsync(); if (closeDeadline is not null && !closed) { keepOpen = true; closeDeadline = null; } }
        finally { completionDialog = null; countdownText = null; }
    }
}
