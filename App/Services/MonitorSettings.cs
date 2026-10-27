using AstralStation.Launch;
using AstralStation.Monitoring;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AstralStation.Services;

// A card in global settings; monitoring never creates launch tasks or executes a candidate.
public sealed class MonitorSettings : UserControl
{
    private readonly string root;
    private readonly Dialogs dialogs;
    private readonly MonitorClient client;
    private readonly StackPanel games = new() { Spacing = 8 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly ToggleSwitch enabled = new() { Header = "持续后台监测", OffContent = "关闭", OnContent = "开启" };
    private bool busy, syncing;
    public MonitorSettings(string root, Dialogs dialogs, MonitorClient client)
    {
        this.root = root; this.dialogs = dialogs; this.client = client;
        var panel = new StackPanel { Spacing = 12 };
        Content = new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = panel };
        panel.Children.Add(new TextBlock { Text = "实时进程监测", Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        panel.Children.Add(new TextBlock { Text = "只监测你明确添加的游戏，每秒检查完整路径和进程身份。关闭主应用后继续记录，启用后随 Windows 登录启动；不会启动或关闭游戏，也不改变今日任务进度。", FontSize = 13, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(enabled); panel.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Content = "手动添加" }; var recommend = new Button { Content = "推荐添加" }; var check = new Button { Content = "检查并恢复后台" };
        actions.Children.Add(add); actions.Children.Add(recommend); actions.Children.Add(check); panel.Children.Add(actions); panel.Children.Add(games);
        add.Click += async (_, _) => await Guard(() => Edit(null));
        recommend.Click += async (_, _) => await Guard(Recommend);
        check.Click += async (_, _) => await Guard(async () => { client.RegisterLogin(); await client.EnsureStarted(); });
        enabled.Toggled += async (_, _) => { if (!syncing) await Guard(() => client.SetEnabled(enabled.IsOn)); };
        Loaded += async (_, _) => await Refresh();
    }
    private async Task Guard(Func<Task> action)
    {
        if (busy || dialogs.Busy) return;
        busy = true; IsEnabled = false; using var scope = dialogs.Interaction();
        try { await action(); }
        catch (Exception ex) { await dialogs.Error(ex.Message); }
        finally { await Refresh(); IsEnabled = true; busy = false; }
    }
    private async Task Refresh()
    {
        syncing = true;
        try
        {
            var config = MonitorData.ReadConfig(root); enabled.IsOn = config.Enabled; games.Children.Clear();
            if (config.Games.Count == 0) games.Children.Add(new TextBlock { Text = "尚未添加监测对象。推荐结果也需手动选择确认，才会加入清单。", TextWrapping = TextWrapping.Wrap });
            foreach (var game in config.Games)
            {
                var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = game.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                text.Children.Add(new PathLabel { Text = game.Path });
                text.Children.Add(new TextBlock { Text = "进程：" + game.ProcessName + ".exe", FontSize = 13 });
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new() { Width = new(40) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(new GameIcon { Path = game.Path, Width = 40, Height = 40 }); row.Children.Add(text); Grid.SetColumn(text, 1);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                var edit = new Button { Content = "编辑" }; var remove = new Button { Content = "移除" };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edit, "编辑监测 " + game.Name);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "移除监测 " + game.Name);
                edit.Click += async (_, _) => await Guard(() => Edit(game));
                remove.Click += async (_, _) => await Guard(async () =>
                {
                    if (!await dialogs.Confirm("移除监测", game.Path + "\n停止此项监测，保留历史记录和正在运行的游戏。")) return;
                    var next = MonitorData.ReadConfig(root); next.Games.RemoveAll(g => g.Id == game.Id); MonitorData.SaveConfig(root, next);
                });
                buttons.Children.Add(edit); buttons.Children.Add(remove); row.Children.Add(buttons); Grid.SetColumn(buttons, 2);
                games.Children.Add(row);
            }
            var state = await client.Status();
            status.Text = state is null ? config.Enabled ? "已启用，但后台未运行。可点击“检查并恢复后台”。" : "后台监测已关闭。清单保留，启用后开始记录。"
                : state.Error ?? $"后台独立运行中 · 正在监测 {state.ActiveCount} 个进程 · {state.UpdatedAt:HH:mm:ss} 检查";
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { syncing = false; }
    }
    private async Task Edit(MonitorGame? old)
    {
        var path = new TextBox { Header = "游戏 EXE 路径", Text = old?.Path ?? "", MinWidth = 420 };
        var process = new TextBox { Header = "进程名称", Text = old?.ProcessName ?? "", PlaceholderText = "不含目录，可省略 .exe" };
        var pick = new Button { Content = "选择 EXE 文件" }; var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        pick.Click += async (_, _) =>
        {
            try { var file = await dialogs.Pick(".exe"); if (file is not null) { path.Text = file; process.Text = Path.GetFileNameWithoutExtension(file); } }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(path); panel.Children.Add(pick); panel.Children.Add(process); panel.Children.Add(error);
        var dialog = new ContentDialog { Title = old is null ? "添加进程监测" : "编辑进程监测", Content = new ScrollViewer { Content = panel }, PrimaryButtonText = "保存", CloseButtonText = "取消" };
        MonitorGame? accepted = null;
        dialog.PrimaryButtonClick += (_, e) =>
        {
            try
            {
                var game = new MonitorGame { Path = path.Text.Trim().Trim('"'), ProcessName = process.Text };
                if (old is not null) game.Id = old.Id;
                if (string.IsNullOrWhiteSpace(game.ProcessName)) game.ProcessName = Path.GetFileNameWithoutExtension(game.Path);
                game.Validate(); if (!File.Exists(game.Path)) throw new FileNotFoundException("游戏 EXE 文件不存在。");
                if (new FileInfo(game.Path).Length == 0) throw new InvalidDataException("不能监测空 EXE 占位文件，请选择实际游戏进程的文件。");
                if (MonitorData.ReadConfig(root).Games.Any(g => g.Id != game.Id && string.Equals(g.Path, game.Path, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("这个 EXE 已在清单中。");
                accepted = game;
            }
            catch (Exception ex) { accepted = null; error.Text = ex.Message; e.Cancel = true; }
        };
        if (await dialogs.Show(dialog) != ContentDialogResult.Primary || accepted is null) return;
        var next = MonitorData.ReadConfig(root);
        if (old is not null && next.Games.RemoveAll(g => g.Id == old.Id) == 0) throw new InvalidDataException("监测清单已改变，请重新打开编辑。");
        next.Games.Add(accepted); MonitorData.SaveConfig(root, next);
    }
    private async Task Recommend()
    {
        using var cancellation = new CancellationTokenSource();
        var label = new TextBlock { Text = "正在检测已安装游戏…", TextWrapping = TextWrapping.Wrap };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 300, MinWidth = 420 };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(label); panel.Children.Add(list);
        var dialog = new ContentDialog { Title = "推荐添加进程监测", Content = panel, PrimaryButtonText = "添加所选", CloseButtonText = "取消", IsPrimaryButtonEnabled = false };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItems.Count > 0;
        async Task Detect()
        {
            try
            {
                var found = await Task.Run(() => Recommendations.Detect(cancellation.Token, monitoring: true));
                if (cancellation.IsCancellationRequested) return;
                var config = MonitorData.ReadConfig(root);
                foreach (var task in found.Where(t => !config.Games.Any(g => string.Equals(g.Path, t.Path, StringComparison.OrdinalIgnoreCase))))
                {
                    if (!File.Exists(task.Path) || new FileInfo(task.Path).Length == 0) continue;
                    var game = new MonitorGame { Path = task.Path, ProcessName = Path.GetFileNameWithoutExtension(task.Path) }; game.Validate();
                    list.Items.Add(new ListViewItem { Content = new TextBlock { Text = task.Name + "\n" + task.Path, TextWrapping = TextWrapping.Wrap }, Tag = game });
                }
                label.Text = list.Items.Count == 0 ? "没有找到可添加的游戏，可返回手动添加。" : "选择要监测的游戏。确认前不会加入清单，也不会启动游戏。";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { label.Text = ex.Message; }
        }
        var work = Detect();
        dialog.Closing += async (_, args) => { cancellation.Cancel(); var deferral = args.GetDeferral(); try { await work; } finally { deferral.Complete(); } };
        if (await dialogs.Show(dialog) != ContentDialogResult.Primary) return;
        var next = MonitorData.ReadConfig(root);
        foreach (var game in list.SelectedItems.Cast<ListViewItem>().Select(i => (MonitorGame)i.Tag))
            if (!next.Games.Any(g => string.Equals(g.Path, game.Path, StringComparison.OrdinalIgnoreCase))) next.Games.Add(game);
        MonitorData.SaveConfig(root, next);
    }
}
