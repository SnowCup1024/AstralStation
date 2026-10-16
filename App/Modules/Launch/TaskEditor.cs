using AstralStation.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AstralStation.Modules.Launch;

public static class TaskEditor
{
    public static async Task<LaunchTask?> Edit(LaunchTask task, Dialogs dialogs, LaunchSession session, bool options = false)
    {
        var next = task.Clone();
        var name = new TextBox { Header = "任务名称", Text = next.Name };
        var path = new TextBox { Header = "启动文件", Text = next.Path, PlaceholderText = "程序、快捷方式或脚本" };
        var processes = new TextBox { Header = "监测进程", Text = string.Join("; ", next.ProcessNames), PlaceholderText = "多个进程用分号分隔，不区分大小写" };
        var reset = new TimePicker { Header = "每日重置时间（本地）", ClockIdentifier = "24HourClock", Time = TimeSpan.FromSeconds(next.ResetSeconds) };
        var enabled = new ToggleSwitch { Header = "参加自动队列", OnContent = "已启用", OffContent = "仅手动启动", IsOn = next.Enabled };
        var args = new TextBox { Header = "启动参数", Text = next.Arguments, MaxLength = 16000 };
        var directory = new TextBox { Header = "工作目录", Text = next.WorkingDirectory, PlaceholderText = "留空使用启动文件所在目录" };
        var delay = new NumberBox { Header = "启动前等待（秒，0–3600）", Value = next.DelaySeconds, Minimum = 0, Maximum = 3600, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var timeout = new NumberBox { Header = "进程检测超时（秒，1–600）", Value = next.TimeoutSeconds, Minimum = 1, Maximum = 600, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var browse = new Button { Content = "选择文件" }; var discover = new Button { Content = "启动并识别进程" }; var cancelDiscovery = new Button { Content = "取消识别", IsEnabled = false };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; actions.Children.Add(browse); actions.Children.Add(discover); actions.Children.Add(cancelDiscovery);
        var panel = new StackPanel { Spacing = 12, MinWidth = 440, MaxWidth = 560 };
        if (!options) foreach (var element in new UIElement[] { name, path, actions, processes, reset }) panel.Children.Add(element);
        foreach (var element in new UIElement[] { enabled, args, directory, delay, timeout, status }) panel.Children.Add(element);
        var dialog = new ContentDialog { Title = options ? "启动选项" : "编辑任务", Content = new ScrollViewer { Content = panel, MaxHeight = 540 }, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.None };
        CancellationTokenSource? cancellation = null; Task? discovery = null; bool accepted = false;
        browse.Click += async (_, _) => { try { string? selected = await dialogs.Pick(".exe", ".lnk", ".bat", ".cmd", ".ps1"); if (selected is null) return; path.Text = selected; if (string.IsNullOrWhiteSpace(name.Text)) name.Text = System.IO.Path.GetFileNameWithoutExtension(selected); if (selected.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) processes.Text = System.IO.Path.GetFileNameWithoutExtension(selected); } catch (Exception ex) { status.Text = ex.Message; } };
        cancelDiscovery.Click += (_, _) => cancellation?.Cancel();
        discover.Click += async (_, _) =>
        {
            cancellation = new(); discover.IsEnabled = browse.IsEnabled = dialog.IsPrimaryButtonEnabled = false; cancelDiscovery.IsEnabled = true;
            session.DiscoveryCancellation = cancellation;
            async Task Identify()
            {
                try { var result = await session.Processes.DiscoverAsync(path.Text, new Progress<string>(s => status.Text = s), cancellation.Token); if (!cancellation.IsCancellationRequested) processes.Text = string.Join("; ", result.ExactMatch is not null ? [result.ExactMatch] : result.Names); status.Text = string.IsNullOrWhiteSpace(result.Notice) ? (cancellation.IsCancellationRequested ? "已取消识别，清理已结束。" : "识别完成，请核对目标进程。") : result.Notice; }
                catch (OperationCanceledException) { status.Text = "已取消识别，临时进程清理已结束。"; }
                catch (Exception ex) { status.Text = ex.Message; }
                finally { discover.IsEnabled = browse.IsEnabled = dialog.IsPrimaryButtonEnabled = true; cancelDiscovery.IsEnabled = false; session.DiscoveryCancellation = null; cancellation.Dispose(); cancellation = null; }
            }
            discovery = Identify(); await discovery;
        };
        dialog.Closing += async (_, e) => { if (discovery is { IsCompleted: false }) { var deferral = e.GetDeferral(); cancellation?.Cancel(); await discovery; deferral.Complete(); } };
        dialog.PrimaryButtonClick += (_, e) =>
        {
            try
            {
                if (double.IsNaN(delay.Value) || double.IsNaN(timeout.Value) || delay.Value != Math.Floor(delay.Value) || timeout.Value != Math.Floor(timeout.Value)) throw new InvalidDataException("等待时间必须为整数。");
                next.Name = string.IsNullOrWhiteSpace(name.Text) ? System.IO.Path.GetFileNameWithoutExtension(path.Text) : name.Text;
                next.Path = path.Text; next.ProcessNames = processes.Text.Split([';', '；'], StringSplitOptions.RemoveEmptyEntries).ToList(); next.ResetSeconds = (int)reset.Time.TotalSeconds;
                next.Enabled = enabled.IsOn; next.Arguments = args.Text; next.WorkingDirectory = directory.Text; next.DelaySeconds = (int)delay.Value; next.TimeoutSeconds = (int)timeout.Value; next.Validate();
                if (next.ProcessNames.Count == 0) throw new InvalidDataException("请填写至少一个监测进程。");
                if (!File.Exists(next.Path)) throw new FileNotFoundException("启动文件不存在。");
                if (next.WorkingDirectory.Length > 0 && !Directory.Exists(next.WorkingDirectory)) throw new DirectoryNotFoundException("工作目录不存在。");
                accepted = true;
            }
            catch (Exception ex) { e.Cancel = true; status.Text = ex.Message; }
        };
        await dialogs.Show(dialog); return accepted ? next : null;
    }
}
