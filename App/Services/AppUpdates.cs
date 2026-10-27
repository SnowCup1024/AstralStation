using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AstralStation.Services;

public static class AppUpdates
{
    public const string Repository = "https://github.com/SnowCup1024/AstralStation";
    public static string DisplayVersion => typeof(AppUpdates).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AstralDisplayVersion").Value!;
    public static string? InstalledExe
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\AstralStation");
            string? root = key?.GetValue("InstallDir") as string;
            if (root is null || !Path.IsPathFullyQualified(root)) return null;
            string exe = Path.Combine(root, "AstralStation.exe");
            return File.Exists(exe) && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) ? exe : null;
        }
    }
    public static bool Installed => InstalledExe is not null;
    private static bool SetupName(string path) => Regex.IsMatch(Path.GetFileName(path), @"^AstralStation-Alpha-[0-9]+(?:\.[0-9]+){1,2}-Setup\.exe$", RegexOptions.CultureInvariant);
    private static void Open(string target) { using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
    public static Border Card(Dialogs dialogs)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "安装与更新", Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        var status = new TextBlock { Text = "安装和更新都使用同一个 Setup.exe。首次安装必须手动选择目录；更新可沿用已有安装位置，安装前会显示确认。卸载时可选择保留或删除本地数据。", TextWrapping = TextWrapping.Wrap };
        var check = new Button { Content = "检查更新" };
        var local = new Button { Content = "打开本地安装包" };
        var download = new Button { Content = "打开下载页", Visibility = Visibility.Collapsed };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(check); buttons.Children.Add(local); buttons.Children.Add(download);
        panel.Children.Add(status); panel.Children.Add(buttons);
        string? releasePage = null;
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false; download.Visibility = Visibility.Collapsed;
            using var scope = dialogs.Interaction();
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AstralStation/" + DisplayVersion);
                using var json = JsonDocument.Parse(await client.GetStringAsync("https://api.github.com/repos/SnowCup1024/AstralStation/releases?per_page=20"));
                string current = typeof(AppUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
                string latest = current;
                foreach (var release in json.RootElement.EnumerateArray())
                {
                    string version = release.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
                    if (release.GetProperty("draft").GetBoolean() || DesktopHost.CompareVersions(version, latest) <= 0 || !release.GetProperty("assets").EnumerateArray().Any(a => SetupName(a.GetProperty("name").GetString() ?? ""))) continue;
                    latest = version;
                    releasePage = release.GetProperty("html_url").GetString();
                }
                download.Visibility = latest == current ? Visibility.Collapsed : Visibility.Visible;
                status.Text = latest == current ? "没有可用的新版 Setup 安装包。" : "可更新至 " + latest + "，请下载并运行同一个安装程序。";
            }
            catch (Exception ex) { status.Text = "检查失败，可重试：" + ex.Message; }
            finally { check.IsEnabled = true; }
        };
        download.Click += async (_, _) => { try { if (releasePage is not null && releasePage.StartsWith(Repository + "/releases/", StringComparison.Ordinal)) Open(releasePage); } catch (Exception ex) { await dialogs.Error(ex.Message); } };
        local.Click += async (_, _) =>
        {
            try
            {
                string? file = await dialogs.Pick(".exe"); if (file is null) return;
                if (!SetupName(file)) throw new InvalidOperationException("请选择 AstralStation-Alpha-版本-Setup.exe 安装包。");
                Open(file);
            }
            catch (Exception ex) { await dialogs.Error(ex.Message); }
        };
        return new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = panel };
    }
}
