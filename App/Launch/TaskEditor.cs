using AstralStation.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AstralStation.Launch;

public static class TaskEditor
{
    public static async Task<IReadOnlyList<LaunchTask>?> Edit(LaunchTask task, Dialogs dialogs, LaunchSession session, bool options = false, bool isNew = false)
    {
        var next = task.Clone();
        var name = new TextBox { Header = "任务名称", Text = next.Name };
        var path = new TextBox { Header = "启动文件", Text = next.Path, PlaceholderText = "程序、快捷方式或脚本" };
        var processes = new TextBox { Header = "监测进程", Text = string.Join("; ", next.ProcessNames), PlaceholderText = "多个进程用分号分隔，不区分大小写" };
        var reset = new TimePicker { Header = "每日重置时间（本地）", ClockIdentifier = "24HourClock", Time = TimeSpan.FromSeconds(next.ResetSeconds) };
        var args = new TextBox { Header = "启动参数", Text = next.Arguments, MaxLength = 16000 };
        var directory = new TextBox { Header = "工作目录", Text = next.WorkingDirectory, PlaceholderText = "留空使用启动文件所在目录" };
        var delay = new NumberBox { Header = "启动前等待（秒，0–3600）", Value = next.DelaySeconds, Minimum = 0, Maximum = 3600, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var timeout = new NumberBox { Header = "进程检测超时（秒，1–600）", Value = next.TimeoutSeconds, Minimum = 1, Maximum = 600, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var browse = new Button { Content = "选择文件" }; var discover = new Button { Content = "启动并识别进程" }; var cancelDiscovery = new Button { Content = "取消识别", IsEnabled = false };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; actions.Children.Add(browse); actions.Children.Add(discover); actions.Children.Add(cancelDiscovery);
        var panel = new StackPanel { Spacing = 12, MinWidth = 440, MaxWidth = 560 };
        void Group(string title, params UIElement[] fields)
        {
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
            foreach (var field in fields) body.Children.Add(field);
            panel.Children.Add(new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = body });
        }
        if (!options) { Group("任务信息", name, path, actions); Group("完成判定", processes, reset); }
        Group("启动方式", args, directory);
        Group("等待与检测", delay, timeout);
        panel.Children.Add(status);
        var form = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var heading = new Grid { Width = 440, ColumnSpacing = 16 };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = options ? "启动选项" : isNew ? "添加任务" : "编辑任务", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var detect = new Button { Content = "检测已安装游戏", FontSize = 14, Visibility = isNew && !options ? Visibility.Visible : Visibility.Collapsed };
        Grid.SetColumn(detect, 1); heading.Children.Add(detect);
        var dialog = new ContentDialog { Title = heading, Content = form, PrimaryButtonText = isNew ? "添加" : "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.None };
        bool detecting = false;
        IReadOnlyList<LaunchTask>? detectedSelection = null;
        using var detectionCancellation = new CancellationTokenSource();
        Task? detectionWork = null;
        var detectionLabel = new TextBlock { Text = "正在检测常用目录、注册表和快捷方式…", TextWrapping = TextWrapping.Wrap };
        var detectedList = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 240, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        detectedList.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Border Background="{ThemeResource CardBackgroundFillColorDefaultBrush}" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1" CornerRadius="8" Padding="12" Margin="0,4">
                <StackPanel Spacing="6"><TextBlock Text="{Binding Name}" FontWeight="SemiBold"/><TextBlock Text="{Binding Path}" FontSize="12" TextWrapping="Wrap" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/></StackPanel>
              </Border>
            </DataTemplate>
            """);
        var detectionPanel = new StackPanel { Spacing = 16, Width = 440 };
        detectionPanel.Children.Add(detectionLabel); detectionPanel.Children.Add(detectedList);
        detectedList.SelectionChanged += (_, _) => { if (detecting) dialog.IsPrimaryButtonEnabled = detectedList.SelectedItems.Count > 0; };
        async Task Detect()
        {
            try
            {
                var found = await Task.Run(() => Recommendations.Detect(detectionCancellation.Token));
                if (detectionCancellation.IsCancellationRequested) return;
                detectedList.ItemsSource = found.Where(t => !session.Store.Tasks.Any(old => string.Equals(System.IO.Path.GetFullPath(old.Path), t.Path, StringComparison.OrdinalIgnoreCase))).ToList();
                detectionLabel.Text = detectedList.Items.Count == 0 ? "没有找到可添加的游戏。可返回手动添加并选择路径。" : "选择要添加的游戏。默认每日 04:00 重置，可在任务库中修改。";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { detectionLabel.Text = ex.Message; }
        }
        detect.Click += (_, _) =>
        {
            detecting = !detecting;
            detect.Content = detecting ? "返回手动添加" : "检测已安装游戏";
            dialog.Content = detecting ? detectionPanel : form;
            dialog.PrimaryButtonText = detecting ? "添加所选" : "添加";
            dialog.IsPrimaryButtonEnabled = !detecting || detectedList.SelectedItems.Count > 0;
            if (detecting && detectionWork is null) detectionWork = Detect();
        };
        CancellationTokenSource? cancellation = null; Task? discovery = null; bool accepted = false;
        browse.Click += async (_, _) => { try { string? selected = await dialogs.Pick(".exe", ".lnk", ".bat", ".cmd", ".ps1"); if (selected is null) return; path.Text = selected; if (string.IsNullOrWhiteSpace(name.Text)) name.Text = System.IO.Path.GetFileNameWithoutExtension(selected); if (selected.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) processes.Text = System.IO.Path.GetFileNameWithoutExtension(selected); } catch (Exception ex) { status.Text = ex.Message; } };
        cancelDiscovery.Click += (_, _) => cancellation?.Cancel();
        discover.Click += async (_, _) =>
        {
            cancellation = new(); detect.IsEnabled = discover.IsEnabled = browse.IsEnabled = dialog.IsPrimaryButtonEnabled = false; cancelDiscovery.IsEnabled = true;
            session.DiscoveryCancellation = cancellation;
            async Task Identify()
            {
                try { var result = await session.Processes.DiscoverAsync(path.Text, new Progress<string>(s => status.Text = s), cancellation.Token); if (!cancellation.IsCancellationRequested) processes.Text = string.Join("; ", result.ExactMatch is not null ? [result.ExactMatch] : result.Names); status.Text = string.IsNullOrWhiteSpace(result.Notice) ? (cancellation.IsCancellationRequested ? "已取消识别，清理已结束。" : "识别完成，请核对目标进程。") : result.Notice; }
                catch (OperationCanceledException) { status.Text = "已取消识别，临时进程清理已结束。"; }
                catch (Exception ex) { status.Text = ex.Message; }
                finally { detect.IsEnabled = discover.IsEnabled = browse.IsEnabled = dialog.IsPrimaryButtonEnabled = true; cancelDiscovery.IsEnabled = false; session.DiscoveryCancellation = null; cancellation.Dispose(); cancellation = null; }
            }
            discovery = Identify(); await discovery;
        };
        dialog.Closing += async (_, e) => { if (discovery is { IsCompleted: false }) { var deferral = e.GetDeferral(); cancellation?.Cancel(); await discovery; deferral.Complete(); } };
        dialog.Closing += async (_, e) => { detectionCancellation.Cancel(); if (detectionWork is { IsCompleted: false }) { var deferral = e.GetDeferral(); await detectionWork; deferral.Complete(); } };
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (detecting) { detectedSelection = detectedList.SelectedItems.Cast<LaunchTask>().ToList(); e.Cancel = detectedSelection.Count == 0; return; }
            try
            {
                if (double.IsNaN(delay.Value) || double.IsNaN(timeout.Value) || delay.Value != Math.Floor(delay.Value) || timeout.Value != Math.Floor(timeout.Value)) throw new InvalidDataException("等待时间必须为整数。");
                next.Name = string.IsNullOrWhiteSpace(name.Text) ? System.IO.Path.GetFileNameWithoutExtension(path.Text) : name.Text;
                next.Path = path.Text; next.ProcessNames = processes.Text.Split([';', '；'], StringSplitOptions.RemoveEmptyEntries).ToList(); next.ResetSeconds = (int)reset.Time.TotalSeconds;
                next.Arguments = args.Text; next.WorkingDirectory = directory.Text; next.DelaySeconds = (int)delay.Value; next.TimeoutSeconds = (int)timeout.Value; next.Validate();
                if (next.ProcessNames.Count == 0) throw new InvalidDataException("请填写至少一个监测进程。");
                if (!File.Exists(next.Path)) throw new FileNotFoundException("启动文件不存在。");
                if (next.WorkingDirectory.Length > 0 && !Directory.Exists(next.WorkingDirectory)) throw new DirectoryNotFoundException("工作目录不存在。");
                accepted = true;
            }
            catch (Exception ex) { e.Cancel = true; status.Text = ex.Message; }
        };
        await dialogs.Show(dialog); return detectedSelection is { Count: > 0 } ? detectedSelection : accepted ? new[] { next } : null;
    }
}
