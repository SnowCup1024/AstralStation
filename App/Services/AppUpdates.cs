using AstralStation.Launch;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace AstralStation.Services;

public static class AppUpdates
{
    public const string Repository = "https://github.com/SnowCup1024/AstralStation";
    public static bool Installed => VelopackLocator.Current.CurrentlyInstalledVersion is not null;
    public static string? InstalledExe => Installed ? Path.Combine(VelopackLocator.Current.AppContentDir!, "AstralStation.exe") : null;

    public static Border Card(LaunchSession session, Dialogs dialogs, Func<Task> prepareRestart)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "安装与更新", Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        var status = new TextBlock { Text = Installed ? "已安装 · Alpha 更新通道" : "当前是开发目录运行。请先运行安装器，再使用应用内更新和登录启动。", TextWrapping = TextWrapping.Wrap };
        var check = new Button { Content = "检查更新", IsEnabled = Installed };
        var local = new Button { Content = "选择本地更新目录", IsEnabled = Installed };
        var apply = new Button { Content = "下载并安装更新", Visibility = Visibility.Collapsed };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(check); buttons.Children.Add(local); buttons.Children.Add(apply);
        panel.Children.Add(status); panel.Children.Add(buttons);
        UpdateManager? manager = null; UpdateInfo? update = null; bool busy = false;
        async Task Check(string? folder)
        {
            if (busy) return;
            busy = true; check.IsEnabled = local.IsEnabled = apply.IsEnabled = false;
            using var scope = dialogs.Interaction();
            try
            {
                status.Text = "正在检查更新…"; update = null; apply.Visibility = Visibility.Collapsed;
                manager = folder is null
                    ? new UpdateManager(new GithubSource(Repository, null, true), new UpdateOptions { ExplicitChannel = "alpha" })
                    : new UpdateManager(folder, new UpdateOptions { ExplicitChannel = "alpha" });
                update = await manager.CheckForUpdatesAsync();
                status.Text = update is null ? "当前已是更新源中的最新版本。" : "可更新至 " + update.TargetFullRelease.Version;
                apply.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex) { status.Text = "检查失败，可重试：" + ex.Message; }
            finally { busy = false; check.IsEnabled = local.IsEnabled = apply.IsEnabled = true; }
        }
        check.Click += async (_, _) => await Check(null);
        local.Click += async (_, _) => { if (busy) return; var folder = await dialogs.PickFolder(); if (folder is not null) await Check(folder); };
        apply.Click += async (_, _) =>
        {
            if (busy || manager is null || update is null || dialogs.Busy) return;
            using var scope = dialogs.Interaction();
            if (!await dialogs.Confirm("安装更新", "将下载更新、保存当前任务的监测状态并重新打开应用。游戏继续运行。")) return;
            busy = true; check.IsEnabled = local.IsEnabled = apply.IsEnabled = false;
            try
            {
                var progress = new Progress<int>(p => status.Text = $"正在下载更新 · {p}%");
                await manager.DownloadUpdatesAsync(update, ((IProgress<int>)progress).Report);
                await prepareRestart();
                manager.ApplyUpdatesAndRestart(update.TargetFullRelease, ["--data-dir", session.Store.Root]);
            }
            catch (Exception ex) { status.Text = "更新未完成，可重试：" + ex.Message; }
            finally { busy = false; check.IsEnabled = local.IsEnabled = apply.IsEnabled = true; }
        };
        return new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = panel };
    }
}
