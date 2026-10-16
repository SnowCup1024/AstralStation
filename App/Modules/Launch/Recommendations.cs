using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AstralStation.Modules.Launch;

public sealed record GamePreset(string Name, string Executable, string[] Folders);
public static class Recommendations
{
    public static readonly GamePreset[] Catalog = [
        new("原神", "YuanShen.exe", ["Genshin Impact Game", "Genshin Impact", "原神", "原神游戏"]),
        new("原神 · 国际服", "GenshinImpact.exe", ["Genshin Impact Game", "Genshin Impact"]),
        new("崩坏：星穹铁道", "StarRail.exe", ["Star Rail Games", "Honkai Star Rail Game", "Honkai Star Rail", "StarRail", "崩坏：星穹铁道", "崩坏 星穹铁道", "星穹铁道"]),
        new("绝区零", "ZenlessZoneZero.exe", ["ZenlessZoneZero Game", "ZenlessZoneZero", "Zenless Zone Zero", "绝区零"]),
        new("崩坏3", "BH3.exe", ["Honkai Impact 3rd Games", "Honkai Impact 3rd", "Honkai Impact 3", "BH3", "崩坏3"]),
        new("明日方舟", "Arknights.exe", ["Arknights", "Arknights Game", "Arknights_CN", "Arknights_EN", "明日方舟"])
    ];
    private static bool Related(string value) => new[] { "原神", "genshin", "hoyoplay", "mihoyo", "米哈游", "honkai", "starrail", "star rail", "星穹铁道", "zenless", "绝区零", "崩坏", "bh3", "arknights", "明日方舟", "endfield", "终末地", "hypergryph", "gryphlink", "鹰角", "yostar" }.Any(w => value.Contains(w, StringComparison.OrdinalIgnoreCase));
    public static List<LaunchTask> Detect(CancellationToken token)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path) { if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path.Trim('"'))) folders.Add(Path.GetFullPath(path.Trim('"'))); }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            foreach (string container in new[] { "", "Games", "Program Files", "Program Files (x86)" })
                foreach (string launcher in new[] { "", "HoYoPlay", "HoYoverse", "miHoYo Launcher", "HoYoverse/miHoYo Launcher", "Hypergryph", "Hypergryph Launcher", "GRYPHLINK", "鹰角网络", "鹰角启动器", "YostarGames", "Arknights/YostarGames" }) Add(Path.Combine(drive.RootDirectory.FullName, container, launcher));
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                    foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                    {
                        using var entry = uninstall!.OpenSubKey(name);
                        if (!Related(entry?.GetValue("DisplayName") as string ?? "")) continue;
                        Add(entry?.GetValue("InstallLocation") as string);
                        if (entry?.GetValue("DisplayIcon") is string icon) Add(Path.GetDirectoryName(icon.Split(',')[0].Trim('"')));
                    }
                    foreach (var game in Catalog) { using var entry = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + game.Executable); if (entry?.GetValue(null) is string path) Add(Path.GetDirectoryName(path.Trim('"'))); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        // Read shortcuts without executing or resolving their targets; bounded traversal only.
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            var pending = new Queue<(string Path, int Depth)>();
            foreach (var kind in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms }) pending.Enqueue((Environment.GetFolderPath(kind), 0));
            int count = 0;
            while (pending.TryDequeue(out var folder) && count < 4096)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(folder.Path))
                    {
                        if (++count > 4096) break;
                        var attributes = File.GetAttributes(path); if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0) { if (folder.Depth < 2) pending.Enqueue((path, folder.Depth + 1)); continue; }
                        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                        object? shortcut = null;
                        try { shortcut = ((dynamic)shell!).CreateShortcut(path); string target = ((dynamic)shortcut).TargetPath; if (Related(path) || Related(target)) { Add(Path.GetDirectoryName(target)); if (string.Equals(Path.GetFileName(Path.GetDirectoryName(target)), "bin", StringComparison.OrdinalIgnoreCase)) Add(Path.GetDirectoryName(Path.GetDirectoryName(target))); } }
                        finally { if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { }
            }
        }
        catch (COMException) { }
        finally { if (shell is not null) Marshal.FinalReleaseComObject(shell); }
        var configs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders) { configs.Add(Path.Combine(folder, "config.ini")); configs.Add(Path.Combine(folder, "config.json")); }
        foreach (var kind in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData })
            foreach (var launcher in new[] { "HYP", "Hypergryph", "GRYPHLINK" })
                foreach (var file in new[] { "config.ini", "config.json" }) configs.Add(Path.Combine(Environment.GetFolderPath(kind), launcher, file));
        foreach (var file in configs)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > 2 * 1024 * 1024) continue;
                string content = File.ReadAllText(file).Replace("\\\"", "\"");
                foreach (Match match in Regex.Matches(content, "\"(?:installPath|persistentInstallPath|gameInstallPath|gamePath|install_path|game_install_path)\"\\s*:\\s*\"(?<path>[^\"\\r\\n]+)\"|^\\s*(?:game_install_path|install_path|installPath|gameInstallPath)\\s*=\\s*(?<ini>[^\\r\\n]+)", RegexOptions.Multiline, TimeSpan.FromSeconds(1))) Add((match.Groups["path"].Success ? match.Groups["path"].Value : match.Groups["ini"].Value).Trim().Trim('"').Replace("\\\\", "\\").Replace("\\/", "/"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { }
        }
        var found = new Dictionary<string, LaunchTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in Catalog) foreach (var folder in folders) foreach (var container in new[] { "", "games", "Game" }) foreach (var suffix in new[] { "" }.Concat(game.Folders))
        {
            token.ThrowIfCancellationRequested(); string path = Path.GetFullPath(Path.Combine(folder, container, suffix, game.Executable));
            if (File.Exists(path)) found.TryAdd(path, new() { Name = game.Name, Path = path, ProcessNames = [Path.GetFileNameWithoutExtension(game.Executable)] });
        }
        return found.Values.ToList();
    }
}
