using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AstralStation.Launch;

// Only the user's Alpha 2.0 list. No emulator, Steam, installer or generic launcher is a game process.
public static class Recommendations
{
    private sealed record Profile(string Name, string[] Aliases, string? Executable = null);
    private sealed record Shortcut(Profile Game, string Path, string Target);
    private static readonly Profile[] Profiles = [
        new("原神", ["Genshin Impact Game", "Genshin Impact", "原神", "原神游戏"], "YuanShen.exe"),
        new("原神 · 国际服", ["Genshin Impact Game", "Genshin Impact"], "GenshinImpact.exe"),
        new("崩坏：星穹铁道", ["Star Rail Games", "Honkai Star Rail Game", "Honkai Star Rail", "StarRail", "崩坏：星穹铁道", "崩坏 星穹铁道", "星穹铁道"], "StarRail.exe"),
        new("绝区零", ["ZenlessZoneZero Game", "ZenlessZoneZero", "Zenless Zone Zero", "绝区零"], "ZenlessZoneZero.exe"),
        new("崩坏3", ["Honkai Impact 3rd Games", "Honkai Impact 3rd", "Honkai Impact 3", "BH3", "崩坏3"], "BH3.exe"),
        new("明日方舟", ["Arknights", "Arknights Game", "Arknights_CN", "Arknights_EN", "明日方舟"], "Arknights.exe"),

        new("重返未来：1999", ["重返未来1999", "Reverse1999", "Reverse 1999"]),
        new("异环", ["异环", "Neverness to Everness", "NTE"]),
        new("无限暖暖", ["无限暖暖", "Infinity Nikki", "InfinityNikki"]),
        new("战双帕弥什", ["战双帕弥什", "Punishing Gray Raven", "PunishingGrayRaven", "PGR"]),
        new("蔚蓝档案（日服）", ["蔚蓝档案", "碧蓝档案", "BlueArchive", "ブルーアーカイブ"]),
        new("少女前线2：追放", ["少女前线2", "少前2", "Girls Frontline 2", "GF2_Exilium", "GF2"]),
        new("胜利女神：NIKKE", ["胜利女神NIKKE", "胜利女神新的希望", "Goddess of Victory NIKKE", "NIKKE"]),
        new("阴阳师", ["阴阳师", "Onmyoji"]),
        new("赛马娘", ["赛马娘", "闪耀优俊少女", "Umamusume", "ウマ娘"])
    ];
    private static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToUpperInvariant();
    private static bool Matches(Profile game, string value)
    {
        if (new[] { "百闻牌", "决战平安京", "阴阳师妖怪屋", "壁纸", "Party Dash", "热血喧闹", "热血ハチャメチャ" }.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase))) return false;
        string normalized = Normalize(value);
        return game.Aliases.Any(alias => { string key = Normalize(alias); return key.Length <= 3 && key.All(char.IsAscii) ? normalized == key : normalized.Contains(key); });
    }
    private static bool Excluded(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => new[] { "steam", "steamapps", "BlueStacks", "MuMu", "LDPlayer", "Nox", "Engine", "Cache", "Logs", "Content", "Paks", "AssetBundles" }.Contains(part, StringComparer.OrdinalIgnoreCase));
    private static bool Helper(string name) => new[] { "launcher", "setup", "install", "update", "unins", "crash", "report", "patch", "cef", "webview", "anticheat", "dmmgameplayer" }
        .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    public static List<LaunchTask> Detect(CancellationToken token)
    {
        var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddHint(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (Path.IsPathFullyQualified(path) && !Excluded(path) && Directory.Exists(path)) hints.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        var roots = Profiles.ToDictionary(p => p, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var shortcuts = new List<Shortcut>();
        void Add(Profile game, string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (!Path.IsPathFullyQualified(path) || Excluded(path)) return;
                path = Path.GetFullPath(path);
                if (Directory.Exists(path) && Path.GetPathRoot(path) != path) roots[game].Add(path);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (string key in uninstall?.GetSubKeyNames() ?? [])
                {
                    token.ThrowIfCancellationRequested();
                    if (key.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)) continue;
                    using var entry = uninstall!.OpenSubKey(key);
                    string name = entry?.GetValue("DisplayName") as string ?? "";
                    if (Launcher(name))
                    {
                        AddHint(entry?.GetValue("InstallLocation") as string);
                        if (entry?.GetValue("DisplayIcon") is string icon) AddHint(Path.GetDirectoryName(icon.Split(',')[0].Trim('"')));
                    }
                    foreach (var game in Profiles.Where(p => Matches(p, name)))
                    {
                        Add(game, entry?.GetValue("InstallLocation") as string);
                        if (entry?.GetValue("DisplayIcon") is string icon)
                        {
                            string file = icon.StartsWith('"') ? icon.Split('"')[1] : icon.Split(',')[0];
                            Add(game, Path.GetDirectoryName(file));
                        }
                    }
                }
                foreach (var game in Profiles.Where(g => g.Executable is not null))
                {
                    using var entry = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + game.Executable);
                    if (entry?.GetValue(null) is string path) Add(game, Path.GetDirectoryName(path.Trim('"')));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        }
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            foreach (var kind in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
            foreach (string file in Files(Environment.GetFolderPath(kind), 3, 2048, token).Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
            {
                var games = Profiles.Where(p => Matches(p, Path.GetFileNameWithoutExtension(file))).ToArray();
                if (games.Length == 0 && !Launcher(file)) continue;
                object? shortcut = null;
                try
                {
                    shortcut = ((dynamic)shell!).CreateShortcut(file);
                    string target = ((dynamic)shortcut).TargetPath;
                    string arguments = ((dynamic)shortcut).Arguments;
                    if (!File.Exists(target) || Excluded(target) || arguments.Contains("steam", StringComparison.OrdinalIgnoreCase)
                        || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        || new[] { "mumu", "bluestacks", "ldplayer", "dnplayer", "nox", "unins", "setup" }.Any(x => Path.GetFileName(target).Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                    if (Launcher(file) || Launcher(target))
                    {
                        string? directory = Path.GetDirectoryName(target); AddHint(directory);
                        if (string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase)) AddHint(Path.GetDirectoryName(directory));
                    }
                    foreach (var game in games)
                    {
                        shortcuts.Add(new(game, file, target));
                        Add(game, Path.GetDirectoryName(target));
                        Add(game, ((dynamic)shortcut).WorkingDirectory as string);
                    }
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException) { }
                finally { if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut); }
            }
        }
        catch (COMException) { }
        finally { if (shell is not null) Marshal.FinalReleaseComObject(shell); }

        // Probe named game directories only; never recursively scan a drive or Program Files.
        var containers = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        foreach (string folder in new[] { "", "Games", "Program Files", "Program Files (x86)", "DMMGames" }) containers.Add(Path.Combine(drive.RootDirectory.FullName, folder));
        containers.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        containers.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DMMGames"));
        foreach (var container in containers)
        foreach (string launcher in new[] { "", "HoYoPlay", "HoYoverse", "miHoYo Launcher", "HoYoverse/miHoYo Launcher", "Hypergryph", "Hypergryph Launcher", "GRYPHLINK", "鹰角网络", "鹰角启动器", "YostarGames", "Arknights/YostarGames" }) AddHint(Path.Combine(container, launcher));
        foreach (var game in Profiles)
        foreach (var container in containers)
        foreach (var alias in game.Aliases) Add(game, Path.Combine(container, alias));

        foreach (var root in roots.Values.SelectMany(r => r)) AddHint(root);

        var configs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in hints) { configs.Add(Path.Combine(folder, "config.ini")); configs.Add(Path.Combine(folder, "config.json")); }
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
                foreach (Match match in Regex.Matches(content, "\"(?:installPath|persistentInstallPath|gameInstallPath|gamePath|install_path|game_install_path)\"\\s*:\\s*\"(?<path>[^\"\\r\\n]+)\"|^\\s*(?:game_install_path|install_path|installPath|gameInstallPath)\\s*=\\s*(?<ini>[^\\r\\n]+)", RegexOptions.Multiline, TimeSpan.FromSeconds(1))) AddHint((match.Groups["path"].Success ? match.Groups["path"].Value : match.Groups["ini"].Value).Trim().Trim('"').Replace("\\\\", "\\").Replace("\\/", "/"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { }
        }

        var result = new Dictionary<string, LaunchTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in Profiles)
        {
            var binaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (game.Executable is not null)
            {
                foreach (var root in hints.Concat(roots[game]).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (string container in new[] { "", "games", "Game" })
                foreach (string suffix in new[] { "" }.Concat(game.Aliases))
                {
                    token.ThrowIfCancellationRequested();
                    string file = Path.Combine(root, container, suffix, game.Executable);
                    if (File.Exists(file)) binaries.Add(file);
                }
            }
            else
            foreach (var root in roots[game])
            foreach (var file in Files(root, 5, 4096, token).Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (Helper(name)) continue;
                try
                {
                    var version = FileVersionInfo.GetVersionInfo(file);
                    if (Helper(version.FileDescription ?? "")) continue;
                    bool identifiedLocation = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => Matches(game, part))
                        || shortcuts.Any(s => s.Game == game && string.Equals(s.Target, file, StringComparison.OrdinalIgnoreCase));
                    bool runtime = name.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)
                        || (File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "UnityPlayer.dll")) && Directory.Exists(Path.Combine(Path.GetDirectoryName(file)!, name + "_Data")));
                    // Identity comes from the actual installed binary, not a guessed EXE name.
                    if (Matches(game, name) || Matches(game, version.ProductName ?? "") || Matches(game, version.FileDescription ?? "") || (identifiedLocation && runtime)) binaries.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            }
            foreach (var file in binaries)
            {
                var links = shortcuts.Where(s => game.Executable is null && s.Game == game && (string.Equals(s.Target, file, StringComparison.OrdinalIgnoreCase)
                    || file.StartsWith(Path.GetDirectoryName(s.Target)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();
                // A remote launcher may live outside the game directory; only use it when unambiguous.
                var link = links.FirstOrDefault() ?? (game.Executable is null && binaries.Count == 1 && shortcuts.Count(s => s.Game == game) == 1 ? shortcuts.Single(s => s.Game == game) : null);
                // DMM authentication cannot be replaced by running umamusume.exe directly.
                if (link is null && (game.Name == "赛马娘" || file.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase))) continue;
                string launch = link?.Path ?? file;
                if (!result.TryGetValue(launch, out var task)) result[launch] = task = new() { Name = game.Name, Path = launch, ProcessNames = [], WorkingDirectory = link is null ? Path.GetDirectoryName(file)! : "" };
                string process = Path.GetFileNameWithoutExtension(file);
                if (!task.ProcessNames.Contains(process, StringComparer.OrdinalIgnoreCase)) task.ProcessNames.Add(process);
            }
        }
        return result.Values.ToList();
    }
    private static bool Launcher(string value) => new[] { "hoyoplay", "mihoyo", "米哈游", "hypergryph", "gryphlink", "鹰角", "yostar" }.Any(w => value.Contains(w, StringComparison.OrdinalIgnoreCase));
    private static IEnumerable<string> Files(string root, int depth, int budget, CancellationToken token)
    {
        var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((root, 0));
        int count = 0;
        while (queue.TryDequeue(out var current) && count < budget)
        {
            token.ThrowIfCancellationRequested();
            string[] entries;
            try
            {
                if (!Directory.Exists(current.Path) || (File.GetAttributes(current.Path) & FileAttributes.ReparsePoint) != 0 || Excluded(current.Path)) continue;
                entries = Directory.EnumerateFileSystemEntries(current.Path).Take(budget - count).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in entries)
            {
                token.ThrowIfCancellationRequested(); count++;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0 || Excluded(file)) continue;
                if ((attributes & FileAttributes.Directory) != 0) { if (current.Depth < depth) queue.Enqueue((file, current.Depth + 1)); }
                else yield return file;
            }
        }
    }
}
