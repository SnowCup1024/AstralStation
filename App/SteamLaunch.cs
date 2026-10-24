// Steam 启动国服：界面、清单、启动脚本与可撤销配置集中在本文件。
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
#if !STEAM_TEST
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AstralStation.Services;
#endif

namespace AstralStation;

internal sealed class SteamLaunch
{
    public const string AppId = "4732690", GameName = "明日方舟：终末地";
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public sealed record Account(string Id, string Name) { public override string ToString() => Name + " · " + Id; }
    public sealed record Depot(string Id, string Manifest);
    public sealed record Metadata(string Name, string InstallDir, string Executable, string Build, List<Depot> Depots);
    public sealed class Settings
    {
        public string SteamPath { get; set; } = "";
        public string Account { get; set; } = "";
        public string Library { get; set; } = "";
        public string Launcher { get; set; } = "";
        public string GameDirectory { get; set; } = "";
        public string Arguments { get; set; } = "--game=endfield --reason=4";
        public int ExitGraceSeconds { get; set; } = 8;
        public bool CloseOwnedLauncher { get; set; } = true;
        public Settings Clone() => (Settings)MemberwiseClone();
    }
    public sealed class Owned
    {
        public string Path { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Original { get; set; }
        public string Applied { get; set; } = "";
    }
    public sealed class Receipt { public List<Owned> Files { get; set; } = []; }
    public sealed record Change(string Path, byte[]? Before, byte[]? After, string Description);
    internal sealed record ComparedLine(string? Before, string? After, bool Changed);
    internal static IEnumerable<ComparedLine> CompareLines(string before, string after)
    {
        string[] Split(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var left = Split(before); var right = Split(after); int i = 0, j = 0;
        while (i < left.Length || j < right.Length)
        {
            if (i < left.Length && j < right.Length && left[i] == right[j]) { yield return new(left[i++], right[j++], false); continue; }
            int skipLeft = i < left.Length && j < right.Length ? Array.IndexOf(left, right[j], i, Math.Min(64, left.Length - i)) : -1;
            int skipRight = i < left.Length && j < right.Length ? Array.IndexOf(right, left[i], j, Math.Min(64, right.Length - j)) : -1;
            if (skipLeft >= 0 && (skipRight < 0 || skipLeft - i <= skipRight - j))
                while (i < skipLeft) yield return new(left[i++], null, true);
            else if (skipRight >= 0)
                while (j < skipRight) yield return new(null, right[j++], true);
            else yield return new(i < left.Length ? left[i++] : null, j < right.Length ? right[j++] : null, true);
        }
    }
    public sealed record Plan(List<Change> Changes, string Description);
    public string Root { get; }
    public string SteamRoot { get; private set; } = "";
    public Settings Config { get; set; }
    public Metadata? Latest { get; private set; }
    public string? Draft { get; private set; }
    private string? draftLibrary;
    private readonly Action checkSteamClosed;
    public List<Account> Accounts { get; private set; } = [];
    public List<string> Libraries { get; private set; } = [];
    public string ScriptPath => Path.Combine(Root, "launch.ps1");
    public string ConfigPath => Path.Combine(Root, "config.json");
    private string ReceiptPath => Path.Combine(Root, "original.json");
    public string AcfPath => Under(Config.Library, "steamapps", $"appmanifest_{AppId}.acf");
    public string ExePath => Under(Config.Library, "steamapps", "common", Latest?.InstallDir ?? InstalledDirectory(), Latest?.Executable ?? "Endfield.exe");
    public string LocalConfigPath => Under(SteamRoot, "userdata", AccountId(Config.Account), "config", "localconfig.vdf");
    public static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
    public string LaunchOptions => $"\"{PowerShellPath}\" -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ScriptPath}\" %command%";
    private static readonly string[] OptionKeys = ["UserLocalConfigStore", "Software", "Valve", "Steam", "apps", AppId, "LaunchOptions"];
    public SteamLaunch(string? root = null, string? steamRoot = null, Action? checkSteamClosed = null)
    {
        this.checkSteamClosed = checkSteamClosed ?? SteamClosed;
        Root = CanonicalPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AstralStation", "SteamLaunch", AppId));
        Config = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath), Json) ?? new() : new();
        Config.Launcher = CanonicalPath(Config.Launcher); Config.GameDirectory = CanonicalPath(Config.GameDirectory);
        Config.Library = CanonicalPath(Config.Library);
        SetSteamRoot(steamRoot ?? (string.IsNullOrWhiteSpace(Config.SteamPath) ? DetectSteam().FirstOrDefault() ?? "" : Config.SteamPath));
    }
    public void SetSteamRoot(string path)
    {
        SteamRoot = Config.SteamPath = CanonicalPath(path);
        string priorLibrary = Config.Library;
        RefreshLocations();
        if (!Accounts.Any(a => a.Id == Config.Account)) Config.Account = Accounts.FirstOrDefault()?.Id ?? "";
        var existing = Libraries.Where(l => File.Exists(Under(l, "steamapps", $"appmanifest_{AppId}.acf"))).ToList();
        if (existing.Count == 1) Config.Library = existing[0];
        else if (!Libraries.Contains(Config.Library, StringComparer.OrdinalIgnoreCase)) Config.Library = Libraries.FirstOrDefault() ?? "";
        if (!string.Equals(priorLibrary, Config.Library, StringComparison.OrdinalIgnoreCase)) { Latest = null; Draft = null; draftLibrary = null; }
    }
    public void RefreshLocations()
    {
        Accounts.Clear(); Libraries.Clear();
        if (!Path.IsPathFullyQualified(SteamRoot) || !File.Exists(Path.Combine(SteamRoot, "steam.exe"))) return;
        var login = Path.Combine(SteamRoot, "config", "loginusers.vdf");
        if (File.Exists(login))
        {
            var users = new Kv(File.ReadAllText(login)).Find("users");
            foreach (var user in (users?.Children ?? []).OrderByDescending(n => n.Get("MostRecent") == "1"))
            {
                if (!ulong.TryParse(user.Key, out ulong steamId) || steamId <= 76561197960265728UL) continue;
                string id = (steamId - 76561197960265728UL).ToString();
                if (File.Exists(Under(SteamRoot, "userdata", id, "config", "localconfig.vdf"))) Accounts.Add(new(id, user.Get("PersonaName") ?? user.Get("AccountName") ?? id));
            }
        }
        Libraries.Add(SteamRoot);
        string libraries = Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraries))
            foreach (var item in new Kv(File.ReadAllText(libraries)).Find("libraryfolders")?.Children ?? [])
            {
                string? path = item.Get("path");
                if (path is not null && Path.IsPathFullyQualified(path) && Directory.Exists(Path.Combine(path, "steamapps")) && !Libraries.Contains(path, StringComparer.OrdinalIgnoreCase)) Libraries.Add(CanonicalPath(path));
            }
    }
    internal static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string full = Path.GetFullPath(path), current = Path.GetPathRoot(full)!;
        if (current.Length >= 2 && current[1] == ':') current = char.ToUpperInvariant(current[0]) + current[1..];
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string? actual = Directory.Exists(current) ? Directory.EnumerateFileSystemEntries(current, part, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 }).FirstOrDefault() : null;
            current = actual ?? Path.Combine(current, part);
        }
        return current;
    }
    public void Require(string action)
    {
        if (action == "options") throw new InvalidOperationException("启动选项只支持复制，请在 Steam 中手动填写。");
        if (action is "remove" or "bootstrap") return;
        if (!Path.IsPathFullyQualified(SteamRoot) || !File.Exists(Path.Combine(SteamRoot, "steam.exe"))) throw new InvalidOperationException("请先选择或检测 Steam 路径。");
        if (!Accounts.Any(a => a.Id == Config.Account)) throw new InvalidOperationException("请先选择 Steam 账号。");
        if (action == "exe" && PlaceholderStatus().BlockReason is string reason) throw new InvalidOperationException(reason);
        ValidatePaths(inspectOnly: action == "inspect-exe");
        if (action is "generate" or "acf" or "runtime")
            if (!File.Exists(ExePath)) throw new InvalidOperationException("请先生成游戏 EXE 占位文件。");
        if (action == "runtime")
            if (!File.Exists(AcfPath)) throw new InvalidOperationException("请先应用 ACF 安装清单。");
        if (action == "acf" && Draft is null) throw new InvalidOperationException("请先获取最新数据并生成 ACF。");
    }
    public string? BlockReason(string action)
    {
        try { Require(action); return null; }
        catch (Exception ex) { return ex.Message; }
    }
    public (string Text, string? BlockReason, bool NonEmpty) PlaceholderStatus()
    {
        string path = ExePath;
        if (!File.Exists(path)) return ("当前不存在", null, false);
        long size = new FileInfo(path).Length;
        string sizeText = size == 0 ? "0 B" : $"{size / 1024d:0.###} KB";
        return ($"当前已存在 {sizeText} 的 {Path.GetFileName(path)} 文件", size == 0 ? "已存在，不需要生成" : "已有非空EXE文件，请检查", size != 0);
    }
    private static string AccountId(string value) => Regex.IsMatch(value, "^[1-9][0-9]{0,9}$") ? value : throw new InvalidOperationException("请选择 Steam 账号。");
    internal static string Under(string root, params string[] parts)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("请选择有效的完整路径。");
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (string part in parts)
        {
            if (string.IsNullOrWhiteSpace(part) || Path.IsPathRooted(part) || part.Split('\\', '/').Any(p => p is ".." or "." || p.Length == 0 || p.EndsWith(' ') || p.EndsWith('.') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)))
                throw new InvalidOperationException("路径包含无效目录或越界内容。");
        }
        string result = Path.GetFullPath(Path.Combine([fullRoot, .. parts]));
        if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("目标路径超出所选目录。");
        NoLinks(result);
        return CanonicalPath(result);
    }
    internal static void NoLinks(string path)
    {
        for (string? item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
        {
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("目标路径经过符号链接或目录联接，无法安全修改：" + item);
        }
    }
    private string InstalledDirectory()
    {
        if (!File.Exists(AcfPath)) return "Arknights Endfield";
        return ReadAcf().Get("installdir") ?? throw new InvalidOperationException("ACF 缺少 installdir，已停止修改。");
    }
    private Kv.Node ReadAcf()
    {
        var state = new Kv(File.ReadAllText(AcfPath)).Find("AppState") ?? throw new InvalidOperationException("ACF 缺少 AppState。");
        if (state.Get("appid") != AppId) throw new InvalidOperationException("ACF 的 appid 与终末地不符。");
        if (!ulong.TryParse(state.Get("SizeOnDisk"), out ulong size)) throw new InvalidOperationException("ACF 的 SizeOnDisk 缺失或无效，已停止修改。");
        return state;
    }
    public string AcfStatus(bool verified)
    {
        if (!File.Exists(AcfPath)) return "当前不存在，可获取、生成并应用。";
        var state = ReadAcf();
        if (ulong.Parse(state.Get("SizeOnDisk")!) != 0) return "当前已存在文件，SizeOnDisk 并非为 0，请卸载 Steam 版本。";
        if (!verified || Latest is null) return "当前已存在文件";
        return state.Get("buildid") == Latest.Build ? "当前已存在文件，已为最新游戏版本。" : "当前已存在文件，已非最新游戏版本，请及时更新。";
    }
    public static Metadata ParseMetadata(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var game = doc.RootElement.GetProperty("data").GetProperty(AppId);
        string Text(JsonElement node, string key) => node.GetProperty(key).ToString();
        static bool Windows(JsonElement node) => !node.TryGetProperty("config", out var c) || !c.TryGetProperty("oslist", out var os) || os.GetString()!.Split(',').Contains("windows");
        var config = game.GetProperty("config");
        var launch = config.GetProperty("launch").EnumerateObject().First(p => Windows(p.Value) && p.Value.TryGetProperty("executable", out _)).Value;
        var depots = game.GetProperty("depots");
        string build = Text(depots.GetProperty("branches").GetProperty("public"), "buildid");
        var manifests = new List<Depot>();
        foreach (var depot in depots.EnumerateObject().Where(p => ulong.TryParse(p.Name, out _) && Windows(p.Value)))
            if (depot.Value.TryGetProperty("manifests", out var m) && m.TryGetProperty("public", out var pub))
                manifests.Add(new(depot.Name, pub.ValueKind == JsonValueKind.Object ? Text(pub, "gid") : pub.ToString()));
        var result = new Metadata(Text(game.GetProperty("common"), "name"), Text(config, "installdir"), Text(launch, "executable"), build, manifests);
        _ = Under(Path.GetTempPath(), result.InstallDir, result.Executable);
        if (result.InstallDir.Contains('\\') || result.InstallDir.Contains('/') || !result.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !ulong.TryParse(build, out ulong b) || b == 0 || manifests.Count == 0 || manifests.Any(d => !ulong.TryParse(d.Manifest, out ulong id) || id == 0)) throw new InvalidOperationException("Steam 返回的构建数据不完整，未生成 ACF。");
        return result;
    }
    public async Task Fetch(bool generate = true, bool complete = false)
    {
        if (generate) Require(complete ? "all" : "generate");
        Latest = null; Draft = null;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        string json = await client.GetStringAsync($"https://api.steamcmd.net/v1/info/{AppId}");
        AcceptMetadata(json, generate);
    }
    internal void AcceptMetadata(string json, bool generate)
    {
        Latest = ParseMetadata(json);
        if (generate) { Draft = GenerateAcf(Latest); draftLibrary = Config.Library; }
    }
    internal string GenerateAcf(Metadata metadata)
    {
        GuardInstallation();
        var acf = new Kv(File.Exists(AcfPath) ? File.ReadAllText(AcfPath) : "\"AppState\"\n{\n}\n");
        // Public universe; fully installed placeholder. No payload has been downloaded.
        var fields = new Dictionary<string, string> { ["appid"] = AppId, ["Universe"] = "1", ["name"] = metadata.Name,
            ["StateFlags"] = "4", ["installdir"] = metadata.InstallDir, ["SizeOnDisk"] = "0", ["buildid"] = metadata.Build,
            ["LastUpdated"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ["AutoUpdateBehavior"] = "1" };
        // 1 = only update on launch; it is not a fictitious "never update" flag.
        foreach (var pair in fields) acf = new Kv(acf.Set(["AppState", pair.Key], pair.Value));
        string depotText = "{\n" + string.Join("\n", metadata.Depots.Select(d => $"\t\t\"{d.Id}\"\n\t\t{{\n\t\t\t\"manifest\" \"{d.Manifest}\"\n\t\t\t\"size\" \"0\"\n\t\t}}")) + "\n\t}";
        return acf.SetRaw(["AppState", "InstalledDepots"], depotText);
    }
    private void GuardInstallation()
    {
        if (!Libraries.Contains(Config.Library, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("所选位置不是 Steam 已登记的安装位置。");
        foreach (string library in Libraries)
            if (!string.Equals(library, Config.Library, StringComparison.OrdinalIgnoreCase) && File.Exists(Under(library, "steamapps", $"appmanifest_{AppId}.acf"))) throw new InvalidOperationException("另一个 Steam 安装位置已有终末地清单，请选择该位置。");
        if (File.Exists(AcfPath) && ulong.Parse(ReadAcf().Get("SizeOnDisk")!) != 0) throw new InvalidOperationException("SizeOnDisk 并非为 0，请卸载 Steam 版本。已拒绝应用。");
        if (File.Exists(ExePath) && new FileInfo(ExePath).Length != 0) throw new InvalidOperationException("危险：目标 EXE 是非空文件。已拒绝生成和应用，绝不会清空现有程序。\n" + ExePath);
        if (File.Exists(AcfPath))
        {
            string prior = Under(Config.Library, "steamapps", "common", InstalledDirectory(), "Endfield.exe");
            if (File.Exists(prior) && new FileInfo(prior).Length != 0) throw new InvalidOperationException("现有 Steam 安装目录包含非空 EXE，已拒绝修改。\n" + prior);
        }
    }
    public void ValidatePaths(bool inspectOnly = false)
    {
        if (inspectOnly)
        {
            if (!Libraries.Contains(Config.Library, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("所选位置不是 Steam 已登记的安装位置。");
        }
        else GuardInstallation();
        foreach (string path in new[] { Config.Launcher, Path.Combine(Config.GameDirectory, "Endfield.exe") })
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidOperationException("未找到有效程序：" + path);
            NoLinks(path);
        }
        if (!Path.GetFileName(Config.Launcher).Equals("Launcher.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择鹰角官方启动器 Launcher.exe。");
        if (Config.GameDirectory.Split('\\', '/').Contains("steamapps", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择国服游戏目录，不能使用 Steam 游戏目录。");
        if (Config.Arguments.Any(c => c is '\r' or '\n' or '\0') || Config.ExitGraceSeconds is < 2 or > 60) throw new InvalidOperationException("脚本参数无效；退出等待应为 2–60 秒。");
    }
    internal SteamLaunch Snapshot()
    {
        var copy = (SteamLaunch)MemberwiseClone();
        copy.Config = Config.Clone();
        copy.Accounts = [.. Accounts]; copy.Libraries = [.. Libraries];
        return copy;
    }
    internal void UseVersion(SteamLaunch snapshot)
    {
        if (snapshot.Latest is null) return;
        if (Latest is not null && Latest.Build != snapshot.Latest.Build) { Draft = null; draftLibrary = null; }
        Latest = snapshot.Latest;
    }
    public string? CurrentOptions() => new Kv(File.ReadAllText(LocalConfigPath)).Find(OptionKeys)?.Value;
    private Receipt ReadReceipt()
    {
        var receipt = File.Exists(ReceiptPath) ? JsonSerializer.Deserialize<Receipt>(File.ReadAllText(ReceiptPath), Json) ?? throw new InvalidOperationException("配置备份损坏。") : new();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in receipt.Files)
        {
            bool allowed = item.Kind switch
            {
                "script" => item.Path.Equals(ScriptPath, StringComparison.OrdinalIgnoreCase),
                "config" => item.Path.Equals(ConfigPath, StringComparison.OrdinalIgnoreCase),
                "acf" => Libraries.Any(l => item.Path.Equals(Under(l, "steamapps", $"appmanifest_{AppId}.acf"), StringComparison.OrdinalIgnoreCase)),
                "exe" => Libraries.Any(l => item.Path.StartsWith(Under(l, "steamapps", "common") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) && Path.GetFileName(item.Path).Equals("Endfield.exe", StringComparison.OrdinalIgnoreCase),
                "options" => Accounts.Any(a => item.Path.Equals(Under(SteamRoot, "userdata", a.Id, "config", "localconfig.vdf"), StringComparison.OrdinalIgnoreCase)),
                _ => false
            };
            if (!allowed || !seen.Add(Path.GetFullPath(item.Path))) throw new InvalidOperationException("原始配置记录中的路径无效或重复，已拒绝修改：" + item.Path);
            NoLinks(item.Path);
        }
        return receipt;
    }
    private static bool OnlyAcfRuntimeChanged(byte[]? before, byte[] applied)
    {
        if (before is null) return false;
        try
        {
            string Normalize(byte[] bytes)
            {
                var state = new Kv(Encoding.UTF8.GetString(bytes)).Find("AppState") ?? throw new FormatException();
                if (state.Get("appid") != AppId || !ulong.TryParse(state.Get("SizeOnDisk"), out var size) || size != 0) throw new FormatException();
                string Canonical(Kv.Node node) => node.Children is null ? JsonSerializer.Serialize(node.Value) : "{" + string.Join(",", node.Children.OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase).Select(n => JsonSerializer.Serialize(n.Key.ToLowerInvariant()) + ":" + Canonical(n))) + "}";
                state.Children!.RemoveAll(n => new[] { "LastPlayed", "LastOwner", "LastUpdated" }.Contains(n.Key, StringComparer.OrdinalIgnoreCase));
                return Canonical(state);
            }
            return Normalize(before) == Normalize(applied);
        }
        catch { return false; }
    }
    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
    private static byte[]? ReadBytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    internal static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    private static void SteamClosed()
    {
        foreach (string name in new[] { "steam", "steamwebhelper" })
        {
            var processes = Process.GetProcessesByName(name);
            bool running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            if (running) throw new InvalidOperationException("请完全退出 Steam 后再应用配置，避免 Steam 覆盖改动。");
        }
    }
    public Plan Prepare(string action)
    {
        Require(action);
        NoLinks(Root);
        var receipt = ReadReceipt(); var changes = new List<Change>(); var details = new List<string>();
        void FileChange(string path, byte[] next, string kind, string label)
        {
            NoLinks(path);
            byte[]? before = ReadBytes(path);
            var owned = receipt.Files.SingleOrDefault(o => o.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (owned is not null && !Equal(before, Convert.FromBase64String(owned.Applied)) && kind != "acf" && !(kind == "script" && before is null)) throw new InvalidOperationException("文件已被外部修改，已拒绝覆盖：" + path);
            if (owned is null) { owned = new() { Path = path, Kind = kind, Original = before is null ? null : Convert.ToBase64String(before) }; receipt.Files.Add(owned); }
            owned.Applied = Convert.ToBase64String(next);
            if (!Equal(before, next)) { changes.Add(new(path, before, next, label)); details.Add($"{label}\n{path}"); }
        }
        if (action == "remove")
        {
            if (receipt.Files.Count == 0) throw new InvalidOperationException("没有由星间驿站应用的配置可移除。");
            foreach (var item in receipt.Files)
            {
                NoLinks(item.Path);
                byte[]? before = ReadBytes(item.Path);
                if (item.Kind == "options") continue;
                {
                    byte[] applied = Convert.FromBase64String(item.Applied);
                    if (!Equal(before, applied) && !(item.Kind == "acf" && OnlyAcfRuntimeChanged(before, applied))) throw new InvalidOperationException("文件已改变，不能自动移除：" + item.Path);
                    byte[]? original = item.Original is null ? null : Convert.FromBase64String(item.Original);
                    changes.Add(new(item.Path, before, original, original is null ? "删除本工具生成的文件" : "恢复原文件"));
                    details.Add((original is null ? "删除：" : "恢复：") + item.Path);
                }
            }
            receipt.Files.RemoveAll(f => f.Kind != "options");
            changes.Add(new(ReceiptPath, ReadBytes(ReceiptPath), receipt.Files.Count == 0 ? null : Utf8(JsonSerializer.Serialize(receipt, Json)), "移除本次配置记录，保留旧版启动选项备份"));
            details.Add("Steam 启动选项请在 Steam 中手动清除或恢复。");
        }
        else
        {
            if (action != "bootstrap") GuardInstallation();
            if (action is "all" or "exe")
            {
                if (Latest is null) throw new InvalidOperationException("请先获取最新数据，确认 Steam 的 EXE 目标路径。");
                FileChange(ExePath, [], "exe", "生成零字节 EXE");
            }
            if (action is "all" or "acf")
            {
                if (Latest is null || Draft is null || draftLibrary != Config.Library) throw new InvalidOperationException("请先点击“获取最新数据并生成”。");
                FileChange(AcfPath, Utf8(GenerateAcf(Latest)), "acf", $"{(File.Exists(AcfPath) ? "更新" : "应用")} ACF，buildid → {Latest.Build}，SizeOnDisk → 0");
            }
            if (action is "all" or "acf" or "runtime")
            {
                ValidatePaths();
                FileChange(ConfigPath, Utf8(JsonSerializer.Serialize(Config, Json)), "config", $"保存启动设置\n官方启动器：{Config.Launcher}\n国服目录：{Config.GameDirectory}\n启动参数：{Config.Arguments}\n退出等待：{Config.ExitGraceSeconds} 秒\n关闭本次新开的启动器：{(Config.CloseOwnedLauncher ? "开启" : "关闭")}");
            }
            if ((action is "all" or "acf" or "runtime" or "bootstrap") && !File.Exists(ScriptPath))
                FileChange(ScriptPath, [239, 187, 191, .. Utf8(Script)], "script", "自动生成启动脚本");
            changes.Add(new(ReceiptPath, ReadBytes(ReceiptPath), Utf8(JsonSerializer.Serialize(receipt, Json)), "保存原始配置备份"));
        }
        return new(changes, string.Join("\n\n", details));
    }
    public void EnsureScript()
    {
        if (!File.Exists(ScriptPath)) Apply(Prepare("bootstrap"));
    }
    public void Apply(Plan plan)
    {
        if (plan.Changes.Any(c => c.Path.EndsWith(".vdf", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("禁止写入 Steam 账号配置；启动选项只能复制后手动填写。");
        if (plan.Changes.Any(c => c.Path.EndsWith(".acf", StringComparison.OrdinalIgnoreCase))) checkSteamClosed();
        using var mutex = new Mutex(false, @"Local\AstralStation.SteamLaunch.Configure");
        bool acquired; try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new InvalidOperationException("另一个配置操作正在进行。");
        var written = new List<Change>();
        try
        {
            foreach (var c in plan.Changes) { NoLinks(c.Path); if (!Equal(ReadBytes(c.Path), c.Before)) throw new InvalidOperationException("确认期间文件发生变化，请重新检查：" + c.Path); }
            foreach (var c in plan.Changes)
            {
                if (!Equal(ReadBytes(c.Path), c.Before)) throw new InvalidOperationException("写入前文件发生变化：" + c.Path);
                if (c.Before is null && c.After is { Length: 0 } && c.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(c.Path)!);
                    using var placeholder = new FileStream(c.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                else Atomic(c.Path, c.After);
                written.Add(c);
            }
        }
        catch (Exception error)
        {
            var failures = new List<string>();
            foreach (var c in written.AsEnumerable().Reverse())
                try { if (!Equal(ReadBytes(c.Path), c.After)) throw new IOException("文件再次变化"); Atomic(c.Path, c.Before); } catch { failures.Add(c.Path); }
            throw new InvalidOperationException(error.Message + (failures.Count == 0 ? "\n未完成的变更已撤回。" : "\n以下文件需保留备份并手动恢复：\n" + string.Join("\n", failures)), error);
        }
        finally { mutex.ReleaseMutex(); }
    }
    private static void Atomic(string path, byte[]? bytes)
    {
        NoLinks(path);
        if (bytes is null) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public bool Configured()
    {
        try
        {
            ValidatePaths(); var receipt = ReadReceipt();
            return receipt.Files.Any(f => f.Kind == "script") && File.Exists(ExePath) && File.Exists(AcfPath) && File.Exists(ScriptPath) && File.Exists(ConfigPath) && File.ReadAllText(ConfigPath) == JsonSerializer.Serialize(Config, Json) && CurrentOptions() == LaunchOptions && ReadAcf().Get("StateFlags") == "4";
        }
        catch { return false; }
    }
    public static List<string> DetectSteam()
    {
        var candidates = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(Path.Combine(path, "steam.exe")))
            {
                string full = CanonicalPath(path);
                if (!candidates.Contains(full, StringComparer.OrdinalIgnoreCase)) candidates.Add(full);
            }
        }
        Add(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hive.OpenSubKey(@"SOFTWARE\Valve\Steam");
            Add(key?.GetValue("InstallPath") as string);
        }
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            foreach (string folder in new[] { "Steam", @"Games\Steam", @"Program Files (x86)\Steam", @"Program Files\Steam" }) Add(Path.Combine(drive.RootDirectory.FullName, folder));
        return candidates;
    }
    public static List<string> Detect(bool launcher, string hint)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        void Add(string? path) { if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && Directory.Exists(path)) roots.Add(path); }
        if (File.Exists(hint)) Add(Path.GetDirectoryName(hint)); else Add(hint);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
            using var key = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = key.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (string child in uninstall?.GetSubKeyNames() ?? [])
            {
                try
                {
                using var entry = uninstall!.OpenSubKey(child);
                string name = entry?.GetValue("DisplayName") as string ?? "";
                if (!new[] { "Hypergryph", "鹰角", "终末地", "Endfield" }.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)) || child.StartsWith("Steam App ")) continue;
                Add(entry?.GetValue("InstallLocation") as string);
                if (entry?.GetValue("DisplayIcon") is string icon) Add(Path.GetDirectoryName(icon.Trim('"').Split(",")[0].Trim('"')));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { errors.Add($"注册表 {child}：{ex.Message}"); }
            }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { errors.Add($"注册表 {hive}/{view}：{ex.Message}"); }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        foreach (string folder in new[] { "Hypergryph", "Hypergryph Launcher", @"Hypergryph\Hypergryph Launcher", @"Games\Hypergryph Launcher", @"Program Files\Hypergryph Launcher", @"Program Files (x86)\Hypergryph Launcher" }) Add(Path.Combine(drive.RootDirectory.FullName, folder));
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        foreach (string suffix in launcher ? new[] { "", "Hypergryph Launcher" } : new[] { "", @"games\Endfield Game", @"games\Arknights Endfield", @"games\EndField", "Endfield Game" })
        {
            string candidate = Path.Combine(root, suffix, launcher ? "Launcher.exe" : "Endfield.exe");
            if (!candidate.Split('\\', '/').Contains("steamapps", StringComparer.OrdinalIgnoreCase) && File.Exists(candidate) && new FileInfo(candidate).Length > 0) found.Add(CanonicalPath(launcher ? candidate : Path.GetDirectoryName(candidate)!));
        }
        if (found.Count == 0 && errors.Count > 0) throw new InvalidOperationException("未识别到安装路径，部分位置读取失败：\n" + string.Join("\n", errors.Distinct().Take(5)));
        return found.Order().ToList();
    }

    // A lossless VDF edit: only the chosen value/range is replaced; comments and other apps survive.
    internal sealed class Kv
    {
        internal sealed class Node(string key, string? value, int start, int valueStart, int end, int close, List<Node>? children)
        {
            public string Key = key; public string? Value = value; public int Start = start, ValueStart = valueStart, End = end, Close = close;
            public List<Node>? Children = children;
            public string? Get(string key) => Children?.SingleOrDefault(n => n.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
        }
        private readonly string text;
        private int position;
        private readonly List<Node> nodes;
        public Kv(string text) { this.text = text; nodes = Parse(false, 0); }
        private void White() { while (position < text.Length) { if (char.IsWhiteSpace(text[position]) || text[position] == '\uFEFF') { position++; continue; } if (text.AsSpan(position).StartsWith("//")) { while (position < text.Length && text[position] != '\n') position++; continue; } break; } }
        private string Token()
        {
            White(); if (position >= text.Length) throw new FormatException("VDF 内容不完整。");
            var value = new StringBuilder();
            if (text[position] != '"') { while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not '{' and not '}') value.Append(text[position++]); if (value.Length == 0) throw new FormatException("VDF 缺少字段。"); return value.ToString(); }
            position++;
            while (position < text.Length)
            {
                char c = text[position++]; if (c == '"') return value.ToString();
                if (c == '\\' && position < text.Length && text[position] is '\\' or '"') c = text[position++];
                value.Append(c);
            }
            throw new FormatException("VDF 引号未闭合。");
        }
        private List<Node> Parse(bool nested, int depth)
        {
            if (depth > 64) throw new FormatException("VDF 嵌套过深。");
            var list = new List<Node>();
            while (true)
            {
                White(); if (position == text.Length) { if (nested) throw new FormatException("VDF 括号未闭合。"); return list; }
                if (text[position] == '}') { if (!nested) throw new FormatException("VDF 多余括号。"); return list; }
                int start = position; string key = Token(); White(); int valueStart = position; string? value = null; List<Node>? children = null; int close = -1;
                if (position < text.Length && text[position] == '{') { position++; children = Parse(true, depth + 1); close = position++; }
                else value = Token();
                list.Add(new(key, value, start, valueStart, position, close, children));
            }
        }
        public Node? Find(params string[] keys)
        {
            List<Node>? current = nodes; Node? result = null;
            foreach (string key in keys) { result = current?.SingleOrDefault(n => n.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); if (result is null) return null; current = result.Children; }
            return result;
        }
        private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        public string Set(string[] keys, string? value) => value is null ? Remove(keys) : SetRaw(keys, Quote(value));
        private string Remove(string[] keys) { var n = Find(keys); return n is null ? text : text.Remove(n.Start, n.End - n.Start); }
        public string SetRaw(string[] keys, string raw)
        {
            var found = Find(keys);
            if (found is not null) return text[..found.ValueStart] + raw + text[found.End..];
            for (int depth = keys.Length - 1; depth >= 0; depth--)
            {
                var parent = depth == 0 ? null : Find(keys[..depth]);
                if (depth != 0 && parent is null) continue;
                if (parent is not null && parent.Children is null) throw new FormatException("VDF 父字段不是区块。");
                string addition = Quote(keys[^1]) + " " + raw;
                for (int i = keys.Length - 2; i >= depth; i--) addition = Quote(keys[i]) + "\n{\n" + addition + "\n}";
                int insert = parent?.Close ?? text.Length;
                return text.Insert(insert, "\n" + addition + "\n");
            }
            throw new FormatException("无法写入 VDF。");
        }
    }

    public const string Script = """
# 星间驿站 · Steam 启动国服 · Alpha 3.0
# Steam 的 %command% 位于 $args 中，故意忽略，不执行占位 EXE。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$mutex = [Threading.Mutex]::new($false, 'Local\AstralStation.SteamLaunch.4732690')
$locked = $false
$tray = $null
$ownedLauncher = $null
$gamePath = $null
function Find-ExactProcess([string]$path) {
    $name = [IO.Path]::GetFileNameWithoutExtension($path)
    foreach ($p in [Diagnostics.Process]::GetProcessesByName($name)) {
        try {
            if ([string]::Equals($p.MainModule.FileName, $path, [StringComparison]::OrdinalIgnoreCase)) {
                $p
            } else { $p.Dispose() }
        } catch {
            $gone = $false
            try { $gone = $p.HasExited } catch { $gone = $true }
            $p.Dispose()
            if (-not $gone) { throw '无法读取游戏或启动器进程路径，请让 Steam 与官方启动器使用相同权限。' }
        }
    }
}


try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw '终末地的 Steam 启动监测已经运行，请查看系统托盘。' }
    $configPath = Join-Path $PSScriptRoot 'config.json'
    $cfg = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $launcherPath = [IO.Path]::GetFullPath($cfg.Launcher)
    $gamePath = [IO.Path]::GetFullPath((Join-Path $cfg.GameDirectory 'Endfield.exe'))
    foreach ($path in @($launcherPath, $gamePath)) {
        if (-not [IO.File]::Exists($path) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "程序不存在或为空：$path"
        }
    }
    $grace = [int]$cfg.ExitGraceSeconds
    if ($grace -lt 2 -or $grace -gt 60) { throw '退出等待必须在 2–60 秒之间。' }
    $tray = [Windows.Forms.NotifyIcon]::new()
    $tray.Icon = [Drawing.SystemIcons]::Application
    $tray.Text = '终末地 · 等待游戏启动'
    $menu = [Windows.Forms.ContextMenuStrip]::new()
    $cancel = $menu.Items.Add('取消等待')
    $script:cancelled = $false
    $cancel.add_Click({ $script:cancelled = $true })
    $tray.ContextMenuStrip = $menu
    $tray.Visible = $true
    $running = @(Find-ExactProcess $gamePath)
    if ($running.Count -eq 0) {
        $previous = @(Find-ExactProcess $launcherPath)
        $hadLauncher = $previous.Count -gt 0
        foreach ($p in $previous) { $p.Dispose() }
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $launcherPath
        $start.WorkingDirectory = [IO.Path]::GetDirectoryName($launcherPath)
        $start.Arguments = [string]$cfg.Arguments
        $start.UseShellExecute = $true
        $started = [Diagnostics.Process]::Start($start)
        if (-not $hadLauncher) { $ownedLauncher = $started }
        elseif ($null -ne $started) { $started.Dispose() }
    }
    foreach ($p in $running) { $p.Dispose() }
    $seen = $false
    $absent = $null
    while (-not $script:cancelled) {
        [Windows.Forms.Application]::DoEvents()
        $running = @(Find-ExactProcess $gamePath)
        if ($running.Count -gt 0) {
            $seen = $true
            $absent = $null
            $cancel.Enabled = $false
            $tray.Text = '终末地 · 游戏运行中'
        } elseif ($seen) {
            if ($null -eq $absent) { $absent = [Diagnostics.Stopwatch]::StartNew() }
            $tray.Text = '终末地 · 等待游戏退出确认'
            if ($absent.Elapsed.TotalSeconds -ge $grace) { break }
        }
        foreach ($p in $running) { $p.Dispose() }
        Start-Sleep -Milliseconds 500
    }
} catch {
    [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Steam 启动国服', 'OK', 'Error') | Out-Null
} finally {
    if ($null -ne $ownedLauncher) {
        try {
            $remaining = @(Find-ExactProcess $gamePath)
            if ($cfg.CloseOwnedLauncher -and $remaining.Count -eq 0 -and -not $ownedLauncher.HasExited) {
                # 仅结束本脚本新建且仍由此进程句柄指向的启动器，不结束游戏或原已运行的启动器。
                if (-not $ownedLauncher.CloseMainWindow() -or -not $ownedLauncher.WaitForExit(3000)) {
                    $ownedLauncher.Kill()
                }
            }
            foreach ($p in $remaining) { $p.Dispose() }
        } catch { }
        $ownedLauncher.Dispose()
    }
    if ($null -ne $tray) { $tray.Visible = $false; $tray.Dispose(); $menu.Dispose() }
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
""";
}

#if !STEAM_TEST
internal sealed class SteamLaunchPage : Grid
{
    private SteamLaunch service = null!;
    private readonly Task initialization;
    private readonly Dialogs dialogs;
    private readonly StackPanel page = new() { Spacing = 20, Margin = new Thickness(24) };
    private readonly StackPanel form = new() { Spacing = 12 };
    private readonly ContentControl formHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar notice = new() { IsClosable = true, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(24) };
    private readonly TextBlock title = new() { Text = "Steam 启动国服", Style = (Style)Application.Current.Resources["PageTitleStyle"] };
    private readonly ComboBox accounts = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 120 };
    private readonly ComboBox libraries = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 120 };
    private Grid? libraryRow;
    private readonly PathLabel steam = PathDisplay(), launcher = PathDisplay(), game = PathDisplay(), exe = PathDisplay(), acf = PathDisplay();
    private readonly List<PathLabel> libraryLabels = [];
    private readonly List<(ContentControl Host, string Action)> steps = [];
    private readonly TextBox options = ReadOnly();
    private readonly TextBlock exeState = new(), acfState = new();
    // The enabled wrapper receives hover events while its child button is disabled.
    private readonly Border exeButtonHost = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Button applyAcf = new() { Content = "应用", IsEnabled = false };
    private readonly Border tile = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Padding = new Thickness(20) };
    private readonly GameIcon icon = new() { Width = 52, Height = 52 };
    private readonly Button back = new() { Content = "返回", Visibility = Visibility.Collapsed };
    private readonly GridView games = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
    private bool busy, initialized;
    private readonly List<(Button Button, string Action)> guarded = [];
    private static TextBox ReadOnly() => new() { IsReadOnly = true, MinWidth = 80 };
    private static PathLabel PathDisplay() => new() { MinWidth = 0, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
    public SteamLaunchPage(Dialogs dialogs, string? testData = null, string? testSteam = null)
    {
        this.dialogs = dialogs;
        initialization = Initialize(testData, testSteam);
    }
    private async Task Initialize(string? testData, string? testSteam)
    {
        try { await InitializePage(testData, testSteam); }
        catch (Exception ex)
        {
            initialized = false; Children.Clear();
            Children.Add(new TextBlock { Text = "Steam 启动国服 · 无法加载页面", Margin = new Thickness(24), FontSize = 23 });
            Children.Add(notice); ShowNotice(ErrorMessage(ex), true);
        }
    }
    private async Task InitializePage(string? testData, string? testSteam)
    {
        service = await Task.Run(() => new SteamLaunch(testData, testSteam));
        Children.Add(new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var heading = new Grid { ColumnSpacing = 16 };
        heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(title); Grid.SetColumn(back, 1); heading.Children.Add(back);
        page.Children.Add(heading);
        // Overlay the viewport: opening or closing a notice must never move form controls.
        Children.Add(notice);
        var tileContent = new StackPanel { Spacing = 12, Width = 150, HorizontalAlignment = HorizontalAlignment.Center };
        tileContent.Children.Add(icon); tileContent.Children.Add(new TextBlock { Text = SteamLaunch.GameName, TextAlignment = TextAlignment.Center });
        games.Resources["GridViewItemCornerRadius"] = tile.CornerRadius;
        var tileStyle = new Style(typeof(GridViewItem)) { BasedOn = (Style)Application.Current.Resources["DefaultGridViewItemStyle"] };
        tileStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, tile.CornerRadius));
        tileStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        tileStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        tileStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        tileStyle.Setters.Add(new Setter(Control.FocusVisualMarginProperty, new Thickness(0)));
        games.ItemContainerStyle = tileStyle;
        tile.Child = tileContent; games.Items.Add(tile); page.Children.Add(games);
        form.Visibility = Visibility.Collapsed; formHost.Content = form; page.Children.Add(formHost);
        games.ItemClick += async (_, _) =>
        {
            games.Visibility = Visibility.Collapsed; form.Visibility = back.Visibility = Visibility.Visible; title.Text = SteamLaunch.GameName;
            await Run(() => Task.Run(service.EnsureScript)); await CheckVersion();
        };
        back.Click += (_, _) => ShowGames();
        ReloadSteamLocations();
        libraries.SizeChanged += (_, _) => { foreach (var label in libraryLabels) label.Width = Math.Max(80, libraries.ActualWidth - 64); };
        launcher.Text = service.Config.Launcher; game.Text = service.Config.GameDirectory;
        var basics = Card("1 · 账号与路径");
        basics.Children.Add(Row("Steam 路径", FieldActions(steam, Button("浏览", async () =>
        {
            string? selected = await dialogs.PickFolder();
            if (selected is not null) await SelectSteam(selected);
        }), Button("自动识别", async () =>
        {
            var found = await Task.Run(SteamLaunch.DetectSteam);
            if (found.Count == 0) throw new InvalidOperationException("未找到 Steam，请浏览选择包含 steam.exe 的安装目录。");
            string? selected = found[0];
            if (found.Count > 1)
            {
                var choice = new ComboBox { ItemsSource = found, SelectedIndex = 0, MaxWidth = 500, HorizontalAlignment = HorizontalAlignment.Stretch };
                selected = await dialogs.Show(new ContentDialog { Title = "选择 Steam 路径", Content = choice, PrimaryButtonText = "选择", CloseButtonText = "取消" }) == ContentDialogResult.Primary ? (string)choice.SelectedItem : null;
            }
            if (selected is not null) await SelectSteam(selected);
        }))));
        basics.Children.Add(Row("Steam 账号", accounts));
        basics.Children.Add(Row("官方启动器", PathInput(launcher, false)));
        basics.Children.Add(Row("国服游戏目录", PathInput(game, true)));
        libraryRow = Row("Steam 安装位置", libraries);
        libraryRow.Visibility = service.Libraries.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        basics.Children.Add(libraryRow);
        var executable = Card("2 · 空EXE占位文件", "inspect-exe");
        executable.Children.Add(Row("游戏 EXE 文件", FieldActions(exe, Button("打开所在目录", () => OpenFolder(service.ExePath)))));
        exeButtonHost.Child = Guarded("生成空EXE文件", "exe", () => Change("exe"));
        executable.Children.Add(Row(exeState, Buttons(exeButtonHost), actions: true));
        var manifest = Card("3 · ACF 安装清单", "generate");
        manifest.Children.Add(Row("ACF 安装清单", FieldActions(acf, Button("查看内容", ViewAcf), Button("打开所在目录", () => OpenFolder(service.AcfPath)))));
        applyAcf.Click += async (_, _) => await Run(() => Change("acf")); guarded.Add((applyAcf, "acf"));
        manifest.Children.Add(Row(acfState, Buttons(Guarded("获取最新数据并生成", "generate", async () => { Pull(); await service.Fetch(); ShowNotice("已生成 ACF 待应用内容，尚未修改 Steam 文件。", false); }), applyAcf), actions: true));
        var launching = Card("4 · Steam 启动选项", "runtime");
        launching.Children.Add(Row("Steam 启动选项", options));
        launching.Children.Add(Row("复制后在 Steam 游戏属性的启动选项中手动填写。启动脚本会自动生成。", Buttons(Guarded("复制", "runtime", async () => { await Task.Run(() => service.Apply(service.Prepare("runtime"))); Dialogs.Copy(service.LaunchOptions); })), actions: true));
        accounts.SelectionChanged += (_, _) => { if (initialized && !busy) { Pull(); Refresh(); } };
        libraries.SelectionChanged += async (_, _) => { if (initialized && !busy) { Pull(); Refresh(); await CheckVersion(); } };
        ActualThemeChanged += (_, _) => Refresh();
        initialized = true; Pull(); Refresh();

    }
    private void ReloadSteamLocations()
    {
        bool wasInitialized = initialized; initialized = false;
        steam.Text = service.SteamRoot;
        accounts.ItemsSource = null; accounts.ItemsSource = service.Accounts;
        accounts.SelectedItem = service.Accounts.FirstOrDefault(a => a.Id == service.Config.Account);
        libraries.Items.Clear(); libraryLabels.Clear();
        foreach (string path in service.Libraries)
        {
            var label = new PathLabel { Text = path, FontSize = 15, Width = Math.Max(80, libraries.ActualWidth - 64) };
            libraryLabels.Add(label); libraries.Items.Add(new ComboBoxItem { Tag = path, Content = label });
        }
        libraries.SelectedItem = libraries.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == service.Config.Library);
        if (libraryRow is not null) libraryRow.Visibility = service.Libraries.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        initialized = wasInitialized;
    }
    private async Task SelectSteam(string path)
    {
        if (!File.Exists(Path.Combine(path, "steam.exe"))) throw new InvalidOperationException("所选目录没有 steam.exe，请选择 Steam 安装目录。");
        Pull();
        // Read into a separate service so malformed account data cannot leave half-updated controls.
        var next = await Task.Run(() =>
        {
            var snapshot = service.Snapshot(); snapshot.SetSteamRoot(path); return snapshot;
        });
        service = next; versionChecks.Clear(); ReloadSteamLocations(); Pull();
        ShowNotice("已识别 Steam 路径，并刷新账号与安装位置。", false);
    }
    public void ShowGames()
    {
        games.Visibility = Visibility.Visible; form.Visibility = back.Visibility = Visibility.Collapsed;
        title.Text = "Steam 启动国服"; if (initialized) notice.IsOpen = false;
    }
    private Task? startupScan;
    public Task ScanOnce() => startupScan ??= ScanPaths();
    private async Task ScanPaths()
    {
        await initialization;
        if (!initialized) return;
        foreach (var item in new[] { (Field: launcher, Launcher: true), (Field: game, Launcher: false) })
        {
            if (!string.IsNullOrWhiteSpace(item.Field.Text)) continue;
            string hint = launcher.Text;
            try
            {
                var found = await Task.Run(() => SteamLaunch.Detect(item.Launcher, hint));
                // Preserve selections made while the background scan was running.
                if (found.Count == 1 && string.IsNullOrWhiteSpace(item.Field.Text)) item.Field.Text = found[0];
            }
            catch { /* Automatic discovery skips unavailable locations; explicit detection reports errors. */ }
        }
        Pull(); Refresh();
    }
    private void Pull()
    {
        service.Config.Account = (accounts.SelectedItem as SteamLaunch.Account)?.Id ?? "";
        service.Config.Library = (libraries.SelectedItem as ComboBoxItem)?.Tag as string ?? service.Config.Library;
        service.Config.Launcher = launcher.Text.Trim().Trim('"'); service.Config.GameDirectory = game.Text.Trim().Trim('"');
        launcher.Text = service.Config.Launcher; game.Text = service.Config.GameDirectory;
        string path = string.IsNullOrWhiteSpace(service.Config.GameDirectory) ? "" : Path.Combine(service.Config.GameDirectory, "Endfield.exe");
        if (icon.Path != path) icon.Path = path;
    }
    private int refreshRevision;
    private bool refreshing;
    private void Refresh()
    {
        refreshRevision++;
        if (initialized && !refreshing) _ = RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        refreshing = true;
        try
        {
            int revision;
            do
            {
                revision = refreshRevision;
                var snapshot = service.Snapshot();
                var actions = guarded.Select(g => g.Action).ToArray();
                var prerequisites = steps.Select(s => s.Action).ToArray();
                try
                {
                    var view = await Task.Run(() =>
                    {
                        var checks = actions.Concat(prerequisites).Distinct().ToDictionary(action => action, snapshot.BlockReason);
                        var reasons = actions.Select(action => checks[action]).ToArray();
                        var stepReasons = prerequisites.Select(action => checks[action]).ToArray();
                        string target = "", manifest = "", manifestState = "请先完成账号与路径配置。";
                        if (Path.IsPathFullyQualified(snapshot.Config.Library))
                        {
                            manifest = snapshot.AcfPath;
                            try { target = snapshot.ExePath; manifestState = snapshot.AcfStatus(snapshot.Latest is not null); }
                            catch (Exception ex) { manifestState = ErrorMessage(ex); }
                        }
                        var placeholder = target.Length == 0 ? (Text: "当前不存在", BlockReason: (string?)null, NonEmpty: false) : snapshot.PlaceholderStatus();
                        return (Reasons: reasons, StepReasons: stepReasons, Exe: target, Acf: manifest, Placeholder: placeholder,
                            AcfState: manifestState, Exists: File.Exists(manifest), Configured: snapshot.Configured());
                    });
                    if (revision != refreshRevision) continue;
                    for (int i = 0; i < guarded.Count; i++)
                    {
                        guarded[i].Button.IsEnabled = view.Reasons[i] is null;
                        ToolTipService.SetToolTip(guarded[i].Button, view.Reasons[i]);
                        if (guarded[i].Action == "exe") ToolTipService.SetToolTip(exeButtonHost, view.Reasons[i]);
                    }
                    for (int i = 0; i < steps.Count; i++)
                    {
                        bool enabled = view.StepReasons[i] is null;
                        steps[i].Host.IsEnabled = enabled; steps[i].Host.Opacity = enabled ? 1 : 0.4;
                        ToolTipService.SetToolTip(steps[i].Host, view.StepReasons[i]);
                    }
                    exe.Text = view.Exe; acf.Text = view.Acf; options.Text = service.LaunchOptions;
                    exeState.Text = view.Placeholder.Text;
                    exeState.Foreground = new SolidColorBrush(view.Placeholder.NonEmpty ? Microsoft.UI.Colors.Firebrick : ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
                    acfState.Text = view.AcfState; applyAcf.Content = view.Exists ? "更新" : "应用";
                    tile.BorderBrush = new SolidColorBrush(view.Configured ? Microsoft.UI.Colors.SeaGreen : Microsoft.UI.Colors.LightGray);
                }
                catch (Exception ex) { if (revision == refreshRevision) ShowNotice(ErrorMessage(ex), true); }
            } while (revision != refreshRevision);
        }
        finally { refreshing = false; }
    }
    private void ShowNotice(string message, bool error)
    {
        notice.Title = "";
        notice.Content = null;
        notice.Message = string.IsNullOrWhiteSpace(message) ? "操作失败，未返回详细信息。" : message;
        notice.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success; notice.IsOpen = true;
    }
    private static string ErrorMessage(Exception error)
    {
        var cause = error.GetBaseException();
        return string.IsNullOrWhiteSpace(cause.Message) ? $"{cause.GetType().Name}（0x{cause.HResult:X8}）：操作失败。" : cause.Message;
    }
    private int operationRevision;
    private readonly Dictionary<string, Task<bool>> versionChecks = new(StringComparer.OrdinalIgnoreCase);
    private async Task CheckVersion()
    {
        try
        {
            Pull();
            string library = service.Config.Library;
            if (!Path.IsPathFullyQualified(library)) return;
            if (!versionChecks.TryGetValue(library, out var work)) versionChecks[library] = work = ReadVersion(service.Snapshot(), library);
            if (!await work && versionChecks.TryGetValue(library, out var current) && ReferenceEquals(current, work)) versionChecks.Remove(library);
        }
        catch (Exception ex) { ShowNotice(ErrorMessage(ex), true); }
    }
    private async Task<bool> ReadVersion(SteamLaunch snapshot, string library)
    {
        int revision = operationRevision;
        try
        {
            await Task.Run(async () => { if (File.Exists(snapshot.AcfPath)) await snapshot.Fetch(false); });
            if (revision != operationRevision || !string.Equals(service.Config.Library, library, StringComparison.OrdinalIgnoreCase)) return false;
            service.UseVersion(snapshot); Refresh();
            return true;
        }
        catch (Exception ex)
        {
            if (form.Visibility == Visibility.Visible && revision == operationRevision && service.Config.Library == library) ShowNotice(ErrorMessage(ex), true);
            return false;
        }
    }
    private async Task Run(Func<Task> operation)
    {
        if (busy) return;
        using var scope = dialogs.Interaction();
        operationRevision++;
        busy = true; formHost.IsEnabled = back.IsEnabled = false; notice.IsOpen = false;
        try { await operation(); }
        catch (Exception ex) { ShowNotice(ErrorMessage(ex), true); }
        finally { busy = false; formHost.IsEnabled = back.IsEnabled = true; Refresh(); }
    }
    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 8, 12, 8) };
        button.Click += async (_, _) => await Run(action); return button;
    }
    private static StackPanel Buttons(params UIElement[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in buttons) panel.Children.Add(b); return panel;
    }
    private static FrameworkElement Right(FrameworkElement control) { control.HorizontalAlignment = HorizontalAlignment.Right; return control; }
    private static Grid Row(string label, FrameworkElement control, bool actions = false) => Row(new TextBlock { Text = label }, control, actions);
    private static Grid Row(TextBlock label, FrameworkElement control, bool actions = false)
    {
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 38 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 100 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = actions ? GridLength.Auto : new GridLength(2.5, GridUnitType.Star), MinWidth = actions ? 0 : 380 });
        label.VerticalAlignment = VerticalAlignment.Center; label.TextWrapping = TextWrapping.Wrap;
        control.VerticalAlignment = VerticalAlignment.Center;
        if (control is StackPanel) control.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(label); Grid.SetColumn(control, 1); grid.Children.Add(control); return grid;
    }
    private static Grid FieldActions(PathLabel field, params Button[] buttons)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = field, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8) });
        var actions = Buttons(buttons); Grid.SetColumn(actions, 1); grid.Children.Add(actions); return grid;
    }
    private Grid PathInput(PathLabel field, bool directory)
    {
        return FieldActions(field, Button("浏览", async () => { string? selected = directory ? await dialogs.PickFolder() : await dialogs.Pick(".exe"); if (selected is not null) field.Text = await Task.Run(() => SteamLaunch.CanonicalPath(selected)); Pull(); }), Button("自动识别", async () =>
        {
            string hint = directory ? launcher.Text : field.Text;
            var found = await Task.Run(() => SteamLaunch.Detect(!directory, hint));
            if (found.Count == 0) throw new InvalidOperationException(directory
                ? "未找到国服 Endfield.exe。已检查官方启动器旁的游戏目录、注册表和常用安装位置；请确认游戏已安装，或点击浏览选择包含 Endfield.exe 的目录。"
                : "未找到官方 Launcher.exe。已检查注册表和常用鹰角安装位置；请确认官方启动器已安装，或点击浏览选择 Launcher.exe。");
            if (found.Count == 1) field.Text = found[0];
            else
            {
                var choice = new ComboBox { ItemsSource = found, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 500 };
                if (await dialogs.Show(new ContentDialog { Title = "选择安装路径", Content = choice, PrimaryButtonText = "选择", CloseButtonText = "取消" }) == ContentDialogResult.Primary) field.Text = (string)choice.SelectedItem;
            }
            Pull();
            ShowNotice((directory ? "已识别国服游戏目录：" : "已识别官方启动器：") + field.Text, false);
        }));
    }
    private StackPanel Card(string title, string? prerequisite = null)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var host = new ContentControl { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (prerequisite is not null) { host.IsEnabled = false; host.Opacity = 0.4; steps.Add((host, prerequisite)); }
        form.Children.Add(new Border { Style = (Style)Application.Current.Resources["SectionCard"], Child = host });
        return content;
    }
    private Button Guarded(string text, string action, Func<Task> operation)
    {
        var button = Button(text, async () => { Pull(); service.Require(action); await operation(); });
        button.IsEnabled = false; guarded.Add((button, action)); return button;
    }
    private static Task OpenFolder(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        while (directory is not null && !Directory.Exists(directory)) directory = Path.GetDirectoryName(directory);
        if (directory is null) throw new InvalidOperationException("找不到目标目录。");
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); return Task.CompletedTask;
    }
    private static TextBlock CodeText(string text) => new() { Text = text, IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 14, Margin = new Thickness(10, 3, 10, 3) };
    private async Task ViewAcf()
    {
        if (!File.Exists(service.AcfPath)) throw new InvalidOperationException("当前文件不存在，请先应用 ACF。");
        var dialog = new ContentDialog { Title = Path.GetFileName(service.AcfPath),
            Content = new ScrollViewer { Content = CodeText(File.ReadAllText(service.AcfPath)), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, CloseButtonText = "关闭" };
        await dialogs.Show(dialog, expand: true);
    }
    private FrameworkElement Preview(SteamLaunch.Plan plan)
    {
        var content = new StackPanel { Spacing = 18 };
        var stroke = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        foreach (var change in plan.Changes.Where(c => !SteamLaunch.Equal(c.Before, c.After)))
        {
            var file = new StackPanel { Spacing = 10 };
            file.Children.Add(new TextBlock { Text = change.Description + "\n" + change.Path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(12) });
            string Decode(byte[]? bytes) => bytes is null ? "（文件不存在）" : bytes.Length == 0 ? "（零字节空文件）" : Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            string before = Decode(change.Before), after = Decode(change.After);
            var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new());
            void Header(int column, string label, string text)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 0, 12, 8) };
                panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                var copy = new Button { Content = "复制全文" }; copy.Click += (_, _) => Dialogs.Copy(text); panel.Children.Add(copy);
                Grid.SetColumn(panel, column); header.Children.Add(panel);
            }
            Header(0, "变更前 · 绿色", before); Header(1, "变更后 · 红色", after); file.Children.Add(header);
            var lines = new Grid(); lines.ColumnDefinitions.Add(new()); lines.ColumnDefinitions.Add(new());
            int row = 0;
            foreach (var line in SteamLaunch.CompareLines(before, after))
            {
                lines.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                void Cell(int column, string? text, bool changed)
                {
                    var border = new Border { Child = CodeText(text ?? ""), BorderBrush = stroke,
                        BorderThickness = new Thickness(column == 1 ? 1 : 0, 0, 0, 0),
                        Background = changed && text is not null ? new SolidColorBrush(column == 0 ? Windows.UI.Color.FromArgb(55, 40, 180, 80) : Windows.UI.Color.FromArgb(55, 230, 60, 60)) : null };
                    Grid.SetRow(border, row); Grid.SetColumn(border, column); lines.Children.Add(border);
                }
                Cell(0, line.Before, line.Changed); Cell(1, line.After, line.Changed); row++;
            }
            file.Children.Add(lines);
            content.Children.Add(new Border { Child = file, BorderBrush = stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(0, 0, 0, 10) });
        }
        return content;
    }
    private async Task Change(string action)
    {
        Pull(); service.Require(action);
        if (action == "exe" && service.Latest is null) await service.Fetch(false);
        var plan = service.Prepare(action);
        if (action == "exe")
        {
            await Task.Run(() => service.Apply(plan));
            ShowNotice("已生成空 EXE 文件：" + service.ExePath, false); return;
        }
        if (string.IsNullOrEmpty(plan.Description)) { ShowNotice("配置已经一致，无需修改。", false); return; }
        var text = Preview(plan);
        var dialog = new ContentDialog { Title = "应用配置 · 变更预览", Content = new ScrollViewer { Content = text }, PrimaryButtonText = "确认应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialogs.Show(dialog, expand: true) != ContentDialogResult.Primary) return;
        await Task.Run(() => service.Apply(plan));
        ShowNotice("文件配置已应用。请复制启动选项并在 Steam 中手动填写。", false);
    }
}
#endif
