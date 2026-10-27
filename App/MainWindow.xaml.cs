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
    private readonly MonitorClient monitor;
    private LaunchPage? launchPage;
    private bool forceExit, closed, keepOpen, restoreAfterRuns, completionEligible;
    private double? closeDeadline;
    private ContentDialog? completionDialog;
    private TextBlock? countdownText;
    private IDisposable? trayScope;
    private string? loginError;
    private readonly Dictionary<long, FocusRequest> running = [];
    private sealed class FocusRequest(LaunchTask task) { public LaunchTask Task = task; public bool Minimized, Attempted; public double Deadline; }
    public MainWindow(string dataRoot, bool paused, bool background, bool loginStartup, string? loginError = null)
    {
        this.loginError = loginError;
        InitializeComponent();
        VersionLabel.Text = "Alpha " + AppUpdates.DisplayVersion;
        SystemBackdrop = new MicaBackdrop();
        Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        session = new(dataRoot, paused, DispatcherQueue);
        monitor = new(dataRoot);
        session.TasksChanged += SyncMonitoring;
        completionEligible = loginStartup || !session.Scheduler.AllDone;
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
        Root.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        UpdateCaptionTheme();
        try { desktop.SetTray(session.Store.Settings.Tray); } catch (Exception ex) { session.Store.Fail(ex.Message); }
        routes["launch"] = () => launchPage = new(session, dialogs);
        routes["calendar"] = Calendar;
        routes["steam"] = () =>
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "--steam-root");
            string? testSteam = at >= 0 && at + 1 < args.Length && args.Contains("--data-dir") ? args[at + 1] : null;
            try { return new SteamLaunchPage(dialogs, testSteam is null ? null : Path.Combine(dataRoot, "SteamLaunch", SteamLaunch.AppId), testSteam); }
            catch (Exception ex) { return Intro("Steam 启动国服", "无法读取配置", ex.Message); }
        };
        routes["settings"] = Settings;
        routes["about"] = () => Intro("关于星间驿站", "Alpha " + AppUpdates.DisplayVersion, "属于自己的游戏工具集合。\n\nC# · WinUI\n仅提供简体中文界面。");
        Navigation.SelectedItem = Navigation.MenuItems[0];
        session.RunsChanged += events => { foreach (var item in events) { if (item.Started) { completionEligible = true; keepOpen = false; if (item.LaunchedHere) running[item.Serial] = new(item.Task); CancelCountdown(); } else running.Remove(item.Serial); } };
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
        Root.Loaded += async (_, _) =>
        {
            session.Start(); if (background && session.Store.Settings.Tray) desktop.Minimize();
            await SyncMonitoringAsync();
            if (!pages.TryGetValue("steam", out var steam)) pages["steam"] = steam = routes["steam"]();
            if (steam is SteamLaunchPage setup) await setup.ScanOnce();
        };
    }
    private async void SyncMonitoring() => await SyncMonitoringAsync();
    private async Task SyncMonitoringAsync()
    {
        try
        {
            if (session.Store.Error is null) monitor.SyncQueue(session.Store.Tasks);
            monitor.RegisterLogin(); await monitor.EnsureStarted();
        }
        catch (Exception ex) { await dialogs.Error("今日队列的进程监测同步失败：" + ex.Message); }
    }
    private void NavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: string key } && pages.TryGetValue(key, out var page)) ResetPage(page);
    }
    private static void ResetPage(FrameworkElement page)
    {
        if (page is SteamLaunchPage steam) steam.ShowGames();
    }
    private void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string key } || !routes.TryGetValue(key, out var factory)) return;
        if (!pages.TryGetValue(key, out var page)) pages[key] = page = factory();
        ResetPage(page);
        PageHost.Content = page;
        PageHost.ContentTransitions = new TransitionCollection { new EntranceThemeTransition() };
        if (session is not null) session.SetManaging(key == "launch" && launchPage?.IsLibrary == true);
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
    private FrameworkElement Calendar()
    {
        var panel = new Grid { Margin = new(16, 12, 16, 12), RowSpacing = 16 };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new());
        panel.Children.Add(new TextBlock { Text = "运行日历", Style = (Style)Application.Current.Resources["PageTitleStyle"] });
        var history = new HistoryView(session, dialogs); panel.Children.Add(history); Grid.SetRow(history, 1);
        panel.Loaded += (_, _) => history.RefreshIfNeeded(true);
        session.Changed += () => { if (panel.IsLoaded) history.RefreshIfNeeded(); };
        return panel;
    }
    private void UpdateCaptionTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        var foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = titleBar.ButtonHoverForegroundColor = titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = dark ? Microsoft.UI.Colors.Gray : Microsoft.UI.Colors.DimGray;
        titleBar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(32, 255, 255, 255) : Windows.UI.Color.FromArgb(24, 0, 0, 0);
        titleBar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(48, 255, 255, 255) : Windows.UI.Color.FromArgb(40, 0, 0, 0);
    }
    private FrameworkElement Settings()
    {
        var panel = Intro("设置", "外观", "");
        var theme = new ComboBox { Header = "应用主题", ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = (int)Root.RequestedTheme, MinWidth = 240 };
        theme.SelectionChanged += async (_, _) => { try { var next = session.Store.Settings.Clone(); next.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "Default" }; session.Store.SaveSettings(next); Root.RequestedTheme = (ElementTheme)theme.SelectedIndex; } catch (Exception ex) { await dialogs.Error(ex.Message); } };
        ((StackPanel)((Border)panel.Children[1]).Child).Children.Add(theme);
        panel.Children.Add(AppUpdates.Card(dialogs));
        var launch = new StackPanel { Spacing = 12 };
        launch.Children.Add(new TextBlock { Text = "启动管理", Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        var autoClose = new ComboBox { ItemsSource = new[] { "保持打开", "倒计时 15 秒后退出" }, Width = 220 };
        var tray = new ComboBox { ItemsSource = new[] { "关闭", "最小化时收入托盘" }, Width = 220 };
        var closeToTray = new ComboBox { ItemsSource = new[] { "直接退出", "最小化" }, Width = 220 };
        var login = new ComboBox { ItemsSource = new[] { "关闭", "开启" }, Width = 220 };
        bool ready = false;
        void Sync()
        {
            ready = false;
            autoClose.SelectedIndex = session.Store.Settings.AutoClose ? 1 : 0;
            tray.SelectedIndex = session.Store.Settings.Tray ? 1 : 0;
            closeToTray.SelectedIndex = session.Store.Settings.CloseToTray ? 1 : 0;
            closeToTray.IsEnabled = session.Store.Settings.Tray;
            try { login.SelectedIndex = desktop.LoginEnabled ? 1 : 0; }
            catch (Exception ex) { loginError = ex.Message; login.SelectedIndex = 0; }
            ready = true;
        }
        void Row(string label, FrameworkElement control)
        {
            var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label);
            Grid.SetColumn(control, 1); row.Children.Add(control); launch.Children.Add(row);
        }
        Row("全部任务完成并退出后", autoClose);
        launch.Children.Add(new TextBlock { Text = "全部任务完成并退出后，自动倒计时 15 秒。只有手动打开时发现今日任务已全部完成，才默认保持打开。任务运行中、记录保存失败或正在操作弹窗时不会退出。", FontSize = 13, TextWrapping = TextWrapping.Wrap });
        Row("允许托盘后台运行", tray); Row("关闭窗口时", closeToTray); Row("Windows 登录时启动", login);
        var loginNotice = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error };
        launch.Children.Add(loginNotice);
        void ShowLoginError() { loginNotice.Message = "登录启动未生效：" + loginError; loginNotice.IsOpen = loginError is not null; }
        async void Save(object sender, SelectionChangedEventArgs args)
        {
            if (!ready) return;
            var old = session.Store.Settings; var next = old.Clone();
            next.AutoClose = autoClose.SelectedIndex == 1; next.Tray = tray.SelectedIndex == 1; next.CloseToTray = next.Tray && closeToTray.SelectedIndex == 1;
            try { desktop.SetTray(next.Tray); session.Store.SaveSettings(next); }
            catch (Exception ex) { desktop.SetTray(old.Tray); await dialogs.Error(ex.Message); }
            finally { Sync(); session.Notify(); }
        }
        autoClose.SelectionChanged += Save; tray.SelectionChanged += Save; closeToTray.SelectionChanged += Save;
        login.SelectionChanged += async (_, _) =>
        {
            if (!ready) return;
            try { desktop.SetLogin(login.SelectedIndex == 1); loginError = null; }
            catch (Exception ex) { loginError = ex.Message; await dialogs.Error(ex.Message); }
            finally { Sync(); ShowLoginError(); }
        };
        Sync(); ShowLoginError();
        panel.Children.Add(new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = launch });
        panel.Children.Add(new MonitorSettings(session.Store.Root, dialogs, monitor));
        var data = new StackPanel { Spacing = 12 };
        data.Children.Add(new TextBlock { Text = "本地数据", Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        data.Children.Add(new TextBlock { Text = session.Store.Root, IsTextSelectionEnabled = true });
        var openData = new Button { Content = "打开数据目录" };
        openData.Click += async (_, _) => { try { DesktopHost.OpenFolder(session.Store.Root); } catch (Exception ex) { await dialogs.Error(ex.Message); } };
        data.Children.Add(openData);
        data.Children.Add(new TextBlock { Text = "任务、状态和历史以 JSON 保存，并保留 .bak 备份。退出应用不会关闭游戏。", FontSize = 13 });
        panel.Children.Add(new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = data });
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private void Restore() { foreach (var request in running.Values) request.Attempted = true; if (session.Scheduler.AllDone) { completionEligible = false; CancelCountdown(); } desktop.Restore(); }
    private void RequestExit() { if (session.Interacting || session.Scheduler.Launching) { Restore(); return; } forceExit = true; completionDialog?.Hide(); Close(); }
    private void CancelCountdown() { closeDeadline = null; completionDialog?.Hide(); }
    private bool CanClose => completionEligible && session.Store.Settings.AutoClose && !keepOpen && session.Scheduler.AllDone && !session.Interacting && session.Store.Error is null;
    private void UpdateLifecycle()
    {
        if (closed) return;
        if (session.Store.Tasks.Any(t => !session.Store.Done(t, DateTime.Now))) { completionEligible = true; keepOpen = false; }
        if (running.Count == 0 && restoreAfterRuns) { restoreAfterRuns = false; desktop.Restore(); }
        if (session.Interacting || session.Store.Error is not null) { CancelCountdown(); return; }
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
        try { await dialog.ShowAsync(); }
        finally { completionDialog = null; countdownText = null; }
    }
}
