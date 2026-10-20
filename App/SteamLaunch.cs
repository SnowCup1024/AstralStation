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
        public string Account { get; set; } = "";
        public string Library { get; set; } = "";
        public string Launcher { get; set; } = "";
        public string GameDirectory { get; set; } = "";
        public string Arguments { get; set; } = "--game=endfield --reason=4";
        public int ExitGraceSeconds { get; set; } = 8;
        public bool CloseOwnedLauncher { get; set; } = true;
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
    public sealed record Plan(List<Change> Changes, string Description);
    public string Root { get; }
    public string SteamRoot { get; }
    public Settings Config { get; set; }
    public Metadata? Latest { get; private set; }
    public string? Draft { get; private set; }
    private string? draftLibrary;
    private readonly Action checkSteamClosed;
    public List<Account> Accounts { get; } = [];
    public List<string> Libraries { get; } = [];
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
        SteamRoot = steamRoot ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ?? "";
        Config = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath), Json) ?? new() : new();
        Config.Launcher = CanonicalPath(Config.Launcher); Config.GameDirectory = CanonicalPath(Config.GameDirectory);
        Config.Library = CanonicalPath(Config.Library);
        if (SteamRoot.Length == 0) return;
        SteamRoot = CanonicalPath(SteamRoot);
        RefreshLocations();
        if (!Accounts.Any(a => a.Id == Config.Account)) Config.Account = Accounts.FirstOrDefault()?.Id ?? "";
        var existing = Libraries.Where(l => File.Exists(Under(l, "steamapps", $"appmanifest_{AppId}.acf"))).ToList();
        if (existing.Count == 1) Config.Library = existing[0];
        else if (!Libraries.Contains(Config.Library, StringComparer.OrdinalIgnoreCase)) Config.Library = Libraries.FirstOrDefault() ?? "";
    }
    public void RefreshLocations()
    {
        Accounts.Clear(); Libraries.Clear();
        if (!File.Exists(Path.Combine(SteamRoot, "steam.exe"))) throw new InvalidOperationException("未找到 Steam，请先安装并登录 Steam。");
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
            string? actual = Directory.Exists(current) ? Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(p => Path.GetFileName(p).Equals(part, StringComparison.OrdinalIgnoreCase)) : null;
            current = actual ?? Path.Combine(current, part);
        }
        return current;
    }
    public void Require(string action)
    {
        if (action == "remove") return;
        if (!Accounts.Any(a => a.Id == Config.Account)) throw new InvalidOperationException("请先选择 Steam 账号。");
        ValidatePaths();
        if (action is "acf" or "script" or "edit" or "options")
            if (!File.Exists(ExePath)) throw new InvalidOperationException("请先生成游戏 EXE 占位文件。");
        if (action is "script" or "edit" or "options")
            if (!File.Exists(AcfPath)) throw new InvalidOperationException("请先应用 ACF 安装清单。");
        if (action == "acf" && Draft is null) throw new InvalidOperationException("请先获取最新数据并生成 ACF。");
        if (action is "edit" or "options")
            if (!File.Exists(ScriptPath) || !File.Exists(ConfigPath)) throw new InvalidOperationException("请先生成启动脚本。");
    }
    public string? BlockReason(string action)
    {
        try { Require(action); return null; }
        catch (Exception ex) { return ex.Message; }
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
    public async Task Fetch(bool generate = true)
    {
        if (generate) Require("generate");
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
    public void ValidatePaths()
    {
        GuardInstallation();
        foreach (string path in new[] { Config.Launcher, Path.Combine(Config.GameDirectory, "Endfield.exe") })
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidOperationException("未找到有效程序：" + path);
            NoLinks(path);
        }
        if (!Path.GetFileName(Config.Launcher).Equals("Launcher.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择鹰角官方启动器 Launcher.exe。");
        if (Config.GameDirectory.Split('\\', '/').Contains("steamapps", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择国服游戏目录，不能使用 Steam 游戏目录。");
        if (Config.Arguments.Any(c => c is '\r' or '\n' or '\0') || Config.ExitGraceSeconds is < 2 or > 60) throw new InvalidOperationException("脚本参数无效；退出等待应为 2–60 秒。");
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
    private static byte[] VdfBytes(byte[] original, string text)
    {
        // Steam's text VDF is UTF-8; retain its BOM when present.
        bool bom = original.AsSpan().StartsWith(new byte[] { 239, 187, 191 });
        return bom ? [239, 187, 191, .. Utf8(text.TrimStart('\uFEFF'))] : Utf8(text);
    }
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
    public Plan Prepare(string action, string? editedScript = null)
    {
        Require(action);
        NoLinks(Root);
        var receipt = ReadReceipt(); var changes = new List<Change>(); var details = new List<string>();
        void FileChange(string path, byte[] next, string kind, string label)
        {
            NoLinks(path);
            byte[]? before = ReadBytes(path);
            var owned = receipt.Files.SingleOrDefault(o => o.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (owned is not null && !Equal(before, Convert.FromBase64String(owned.Applied)) && kind != "acf") throw new InvalidOperationException("文件已被外部修改，已拒绝覆盖：" + path);
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
                if (item.Kind == "options")
                {
                    if (before is null) throw new InvalidOperationException("Steam 账号配置已不存在：" + item.Path);
                    var vdf = new Kv(Encoding.UTF8.GetString(before));
                    if (vdf.Find(OptionKeys)?.Value != item.Applied) throw new InvalidOperationException("Steam 启动选项已被另行修改，拒绝覆盖：" + item.Path);
                    changes.Add(new(item.Path, before, VdfBytes(before, vdf.Set(OptionKeys, item.Original)), "恢复原启动选项"));
                    details.Add($"恢复启动选项：{item.Path}\n当前：{item.Applied}\n恢复：{item.Original ?? "（未设置）"}");
                }
                else
                {
                    byte[] applied = Convert.FromBase64String(item.Applied);
                    if (!Equal(before, applied) && !(item.Kind == "acf" && OnlyAcfRuntimeChanged(before, applied))) throw new InvalidOperationException("文件已改变，不能自动移除：" + item.Path);
                    byte[]? original = item.Original is null ? null : Convert.FromBase64String(item.Original);
                    changes.Add(new(item.Path, before, original, original is null ? "删除本工具生成的文件" : "恢复原文件"));
                    details.Add((original is null ? "删除：" : "恢复：") + item.Path);
                }
            }
            changes.Add(new(ReceiptPath, ReadBytes(ReceiptPath), null, "移除本次配置记录"));
        }
        else
        {
            GuardInstallation();
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
            if (action is "all" or "script" or "edit")
            {
                ValidatePaths();
                FileChange(ConfigPath, Utf8(JsonSerializer.Serialize(Config, Json)), "config", $"保存启动设置\n官方启动器：{Config.Launcher}\n国服目录：{Config.GameDirectory}\n启动参数：{Config.Arguments}\n退出等待：{Config.ExitGraceSeconds} 秒\n关闭本次新开的启动器：{(Config.CloseOwnedLauncher ? "开启" : "关闭")}");
                // Keep user's edited script when applying the complete configuration again.
                string script = editedScript ?? (action == "all" && File.Exists(ScriptPath) ? File.ReadAllText(ScriptPath) : Script);
                FileChange(ScriptPath, [239, 187, 191, .. Utf8(script.TrimStart('\uFEFF'))], "script", editedScript is not null ? "保存编辑后的脚本" : action == "script" && File.Exists(ScriptPath) ? "用 Alpha 3.0 内置逻辑重新生成脚本（替换已有脚本）" : "生成 / 保留启动脚本");
            }
            if (action is "all" or "options")
            {
                ValidatePaths();
                if (action == "options" && (!File.Exists(ScriptPath) || !File.Exists(ConfigPath))) throw new InvalidOperationException("请先生成启动脚本。");
                string path = LocalConfigPath; NoLinks(path);
                byte[] before = File.ReadAllBytes(path);
                var vdf = new Kv(Encoding.UTF8.GetString(before));
                string? current = vdf.Find(OptionKeys)?.Value;
                var owned = receipt.Files.SingleOrDefault(o => o.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (owned is not null && current != owned.Applied) throw new InvalidOperationException("Steam 启动选项已被另行修改，请先核对并恢复已应用的选项。");
                if (owned is null) { owned = new() { Path = path, Kind = "options", Original = current }; receipt.Files.Add(owned); }
                owned.Applied = LaunchOptions;
                changes.Add(new(path, before, VdfBytes(before, vdf.Set(OptionKeys, LaunchOptions)), "写入 Steam 启动选项"));
                details.Add($"Steam 账号：{Accounts.FirstOrDefault(a => a.Id == Config.Account)}\n{path}\n当前：{current ?? "（未设置）"}\n将改为：{LaunchOptions}");
            }
            changes.Add(new(ReceiptPath, ReadBytes(ReceiptPath), Utf8(JsonSerializer.Serialize(receipt, Json)), "保存原始配置备份"));
        }
        return new(changes, string.Join("\n\n", details));
    }
    public void Apply(Plan plan)
    {
        if (plan.Changes.Any(c => c.Path.EndsWith(".acf", StringComparison.OrdinalIgnoreCase) || c.Path.EndsWith("localconfig.vdf", StringComparison.OrdinalIgnoreCase))) checkSteamClosed();
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
            return receipt.Files.Any(f => f.Kind == "options" && f.Path.Equals(LocalConfigPath, StringComparison.OrdinalIgnoreCase)) && File.Exists(ExePath) && File.Exists(AcfPath) && File.Exists(ScriptPath) && File.Exists(ConfigPath) && File.ReadAllText(ConfigPath) == JsonSerializer.Serialize(Config, Json) && CurrentOptions() == LaunchOptions && ReadAcf().Get("StateFlags") == "4";
        }
        catch { return false; }
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
        private void White() { while (position < text.Length) { if (char.IsWhiteSpace(text[position]) || text[position] == '\uFEFF') { position++; continue; } if (text[position..].StartsWith("//")) { while (position < text.Length && text[position] != '\n') position++; continue; } break; } }
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
    private readonly SteamLaunch service;
    private readonly Dialogs dialogs;
    private readonly StackPanel page = new() { Spacing = 20, Margin = new Thickness(24) };
    private readonly StackPanel form = new() { Spacing = 12 };
    private readonly ContentControl formHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar notice = new() { IsClosable = true, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(24) };
    private readonly TextBlock title = new() { Text = "Steam 启动国服", Style = (Style)Application.Current.Resources["PageTitleStyle"] };
    private readonly ComboBox accounts = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 120 };
    private readonly ComboBox libraries = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 120 };
    private readonly PathLabel launcher = PathDisplay(), game = PathDisplay(), exe = PathDisplay(), acf = PathDisplay(), script = PathDisplay();
    private readonly TextBox options = ReadOnly();
    private readonly TextBlock exeState = new(), acfState = new();
    private readonly Button applyAcf = new() { Content = "应用" };
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
        service = new(testData, testSteam);
        Children.Add(new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var heading = new Grid { ColumnSpacing = 16 };
        heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(title); Grid.SetColumn(back, 1); heading.Children.Add(back);
        page.Children.Add(heading);
        // Overlay the viewport: opening or closing a notice must never move form controls.
        Children.Add(notice);
        var tileContent = new StackPanel { Spacing = 12, Width = 150, HorizontalAlignment = HorizontalAlignment.Center };
        tileContent.Children.Add(icon); tileContent.Children.Add(new TextBlock { Text = SteamLaunch.GameName, TextAlignment = TextAlignment.Center });
        tile.Child = tileContent; games.Items.Add(tile); page.Children.Add(games);
        form.Visibility = Visibility.Collapsed; formHost.Content = form; page.Children.Add(formHost);
        games.ItemClick += async (_, _) => { games.Visibility = Visibility.Collapsed; form.Visibility = back.Visibility = Visibility.Visible; title.Text = SteamLaunch.GameName; await DetectMissingPaths(); await CheckVersion(); };
        back.Click += (_, _) => { games.Visibility = Visibility.Visible; form.Visibility = back.Visibility = Visibility.Collapsed; title.Text = "Steam 启动国服"; Refresh(); };
        accounts.ItemsSource = service.Accounts; accounts.SelectedItem = service.Accounts.FirstOrDefault(a => a.Id == service.Config.Account);
        var libraryLabels = new List<PathLabel>();
        foreach (string path in service.Libraries) { var label = new PathLabel { Text = path, FontSize = 15, Width = 260 }; libraryLabels.Add(label); libraries.Items.Add(new ComboBoxItem { Tag = path, Content = label }); }
        libraries.SelectedItem = libraries.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == service.Config.Library);
        libraries.SizeChanged += (_, _) => { foreach (var label in libraryLabels) label.Width = Math.Max(80, libraries.ActualWidth - 64); };
        launcher.Text = service.Config.Launcher; game.Text = service.Config.GameDirectory;
        var basics = Card("1 · 账号与国服路径");
        basics.Children.Add(Row("Steam 账号", accounts));
        if (service.Libraries.Count > 1) basics.Children.Add(Row("Steam 安装位置", libraries));
        basics.Children.Add(Row("官方启动器", PathInput(launcher, false)));
        basics.Children.Add(Row("国服游戏目录", PathInput(game, true)));
        var executable = Card("2 · Steam 游戏占位文件");
        executable.Children.Add(Row("游戏 EXE 文件", FieldActions(exe, Button("打开所在目录", () => OpenFolder(service.ExePath)))));
        executable.Children.Add(Row(exeState, Buttons(Guarded("生成", "exe", () => Change("exe")))));
        var manifest = Card("3 · ACF 安装清单");
        manifest.Children.Add(Row("ACF 安装清单", FieldActions(acf, Button("查看内容", ViewAcf))));
        applyAcf.Click += async (_, _) => await Run(() => Change("acf")); guarded.Add((applyAcf, "acf"));
        manifest.Children.Add(Row(acfState, Buttons(Guarded("获取最新数据并生成", "generate", async () => { Pull(); await service.Fetch(); ShowNotice("已生成 ACF 待应用内容，尚未修改 Steam 文件。", false); }), applyAcf)));
        var scripting = Card("4 · 启动脚本");
        scripting.Children.Add(Row("启动脚本", FieldActions(script, Button("打开所在目录", () => OpenFolder(service.ScriptPath)))));
        scripting.Children.Add(Right(Buttons(Guarded("生成", "script", () => Change("script")), Guarded("编辑", "edit", EditScript))));
        var launching = Card("5 · Steam 启动选项");
        launching.Children.Add(Row("Steam 启动选项", options));
        launching.Children.Add(Right(Buttons(Guarded("写入 Steam", "options", () => Change("options")), Guarded("复制", "options", () => { Dialogs.Copy(service.LaunchOptions); return Task.CompletedTask; }))));
        var complete = Card("整套配置");
        complete.Children.Add(Row("填写账号与国服路径后，可按顺序一键应用。", Buttons(Button("一键移除配置", () => Change("remove")), Guarded("一键应用配置", "all", () => Change("all")))));
        accounts.SelectionChanged += (_, _) => { if (initialized && !busy) { Pull(); Refresh(); } };
        libraries.SelectionChanged += async (_, _) => { if (initialized && !busy) { Pull(); await CheckVersion(); } };
        initialized = true; Refresh();
        Loaded += (_, _) => { Pull(); Refresh(); }; // Resolve the launch command against this Windows user's current AppData path.
    }
    private async Task DetectMissingPaths() => await Run(async () =>
    {
        var messages = new List<string>();
        foreach (var item in new[] { (Field: launcher, Launcher: true), (Field: game, Launcher: false) })
        {
            if (!string.IsNullOrWhiteSpace(item.Field.Text)) continue;
            string hint = launcher.Text;
            var found = await Task.Run(() => SteamLaunch.Detect(item.Launcher, hint));
            string name = item.Launcher ? "官方启动器" : "国服游戏目录";
            if (found.Count == 1) item.Field.Text = found[0];
            else messages.Add(found.Count == 0 ? $"未找到{name}，请浏览选择。" : $"找到多个{name}，请点击自动识别选择。");
        }
        Pull();
        if (messages.Count > 0) ShowNotice(string.Join("\n", messages), true);
    });
    private void Pull()
    {
        service.Config.Account = (accounts.SelectedItem as SteamLaunch.Account)?.Id ?? "";
        service.Config.Library = (libraries.SelectedItem as ComboBoxItem)?.Tag as string ?? service.Config.Library;
        service.Config.Launcher = SteamLaunch.CanonicalPath(launcher.Text.Trim().Trim('"')); service.Config.GameDirectory = SteamLaunch.CanonicalPath(game.Text.Trim().Trim('"'));
        launcher.Text = service.Config.Launcher; game.Text = service.Config.GameDirectory;
    }
    private void Refresh()
    {
        foreach (var item in guarded)
        {
            string? reason = service.BlockReason(item.Action);
            item.Button.IsEnabled = reason is null; ToolTipService.SetToolTip(item.Button, reason);
        }
        try
        {
            exe.Text = service.ExePath; acf.Text = service.AcfPath; script.Text = service.ScriptPath; options.Text = service.LaunchOptions;
            exeState.Text = !File.Exists(service.ExePath) ? "当前不存在" : new FileInfo(service.ExePath).Length == 0 ? "当前已存在" : "存在非空文件，已拒绝生成和应用。";
            exeState.Foreground = new SolidColorBrush(exeState.Text.StartsWith("存在非空") ? Microsoft.UI.Colors.Firebrick : ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
            acfState.Text = service.AcfStatus(service.Latest is not null);
            applyAcf.Content = File.Exists(service.AcfPath) ? "更新" : "应用";
            tile.BorderBrush = new SolidColorBrush(service.Configured() ? Microsoft.UI.Colors.SeaGreen : Microsoft.UI.Colors.LightGray);
            icon.Path = Path.Combine(service.Config.GameDirectory, "Endfield.exe");
        }
        catch (Exception ex) { ShowNotice(ErrorMessage(ex), true); }
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
    private async Task CheckVersion() { Pull(); Refresh(); if (File.Exists(service.AcfPath)) await Run(() => service.Fetch(false)); }
    private async Task Run(Func<Task> operation)
    {
        if (busy) return;
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
    private static Grid Row(string label, FrameworkElement control) => Row(new TextBlock { Text = label }, control);
    private static Grid Row(TextBlock label, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 38 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 100 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.5, GridUnitType.Star), MinWidth = 380 });
        label.VerticalAlignment = VerticalAlignment.Center; label.TextWrapping = TextWrapping.Wrap;
        control.VerticalAlignment = VerticalAlignment.Center;
        if (control is StackPanel) control.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(label); Grid.SetColumn(control, 1); grid.Children.Add(control); return grid;
    }
    private static Grid FieldActions(PathLabel field, params Button[] buttons)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border { Child = field, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] });
        var actions = Buttons(buttons); Grid.SetColumn(actions, 1); grid.Children.Add(actions); return grid;
    }
    private Grid PathInput(PathLabel field, bool directory)
    {
        return FieldActions(field, Button("浏览", async () => { string? selected = directory ? await dialogs.PickFolder() : await dialogs.Pick(".exe"); if (selected is not null) field.Text = selected; Pull(); }), Button("自动识别", async () =>
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
    private StackPanel Card(string title)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        form.Children.Add(new Border { Child = content, Padding = new Thickness(16), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] });
        return content;
    }
    private Button Guarded(string text, string action, Func<Task> operation)
    {
        var button = Button(text, async () => { Pull(); service.Require(action); await operation(); });
        guarded.Add((button, action)); return button;
    }
    private static Task OpenFolder(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        while (directory is not null && !Directory.Exists(directory)) directory = Path.GetDirectoryName(directory);
        if (directory is null) throw new InvalidOperationException("找不到目标目录。");
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); return Task.CompletedTask;
    }
    private async Task ViewAcf()
    {
        var content = new StackPanel { Spacing = 12 };
        void Add(string label, string text) { content.Children.Add(new TextBlock { Text = label }); content.Children.Add(new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"), FontSize = 13, Height = 230, MinWidth = 440 }); }
        if (File.Exists(service.AcfPath)) Add("当前文件", File.ReadAllText(service.AcfPath));
        if (service.Draft is not null) Add("待应用内容", service.Draft);
        if (content.Children.Count == 0) throw new InvalidOperationException("当前没有 ACF，请先获取最新数据并生成。");
        await dialogs.Show(new ContentDialog { Title = "ACF 安装清单", Content = new ScrollViewer { Content = content }, CloseButtonText = "关闭" });
    }
    private async Task Change(string action, string? edited = null)
    {
        Pull(); service.Require(action);
        if (action == "all" && service.Draft is null) await service.Fetch();
        if (action == "exe" && service.Latest is null) await service.Fetch(false);
        var plan = service.Prepare(action, edited);
        if (string.IsNullOrEmpty(plan.Description)) { ShowNotice("配置已经一致，无需修改。", false); return; }
        var text = new TextBlock { Text = plan.Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 580, IsTextSelectionEnabled = true };
        var dialog = new ContentDialog { Title = action == "remove" ? "移除配置 · 变更预览" : "应用配置 · 变更预览", Content = new ScrollViewer { Content = text }, PrimaryButtonText = action == "remove" ? "确认移除" : "确认应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialogs.Show(dialog) != ContentDialogResult.Primary) return;
        await Task.Run(() => service.Apply(plan));
        ShowNotice(action == "remove" ? "配置已移除，原有文件和启动选项已恢复。" : "配置已应用。", false);
    }
    private async Task EditScript()
    {
        Pull();
        var body = new StackPanel { Spacing = 12, MinWidth = 440 };
        var args = new TextBox { Text = service.Config.Arguments };
        var grace = new NumberBox { Value = service.Config.ExitGraceSeconds, Minimum = 2, Maximum = 60, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var cleanup = new ToggleSwitch { IsOn = service.Config.CloseOwnedLauncher, OnContent = "开启", OffContent = "关闭" };
        // The editor is the only deliberately multiline control: it edits script source.
        void Setting(string label, FrameworkElement control)
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(control, 1); row.Children.Add(control); body.Children.Add(row);
        }
        Setting("启动器参数", args); Setting("退出确认等待（秒）", grace); Setting("结束后关闭本次新开的启动器", cleanup);
        var editor = new TextBox { Text = File.Exists(service.ScriptPath) ? File.ReadAllText(service.ScriptPath) : SteamLaunch.Script, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"), FontSize = 13, Height = 270, MaxWidth = 580 };
        body.Children.Add(editor);
        var error = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.Firebrick) }; body.Children.Add(error);
        var dialog = new ContentDialog { Title = "编辑启动脚本", Content = new ScrollViewer { Content = body }, PrimaryButtonText = "检查并保存", CloseButtonText = "取消" };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try { await ValidateScript(editor.Text); }
            catch (Exception ex) { e.Cancel = true; error.Text = ex.Message; }
            finally { deferral.Complete(); }
        };
        if (await dialogs.Show(dialog) != ContentDialogResult.Primary) return;
        service.Config.Arguments = args.Text; service.Config.ExitGraceSeconds = (int)grace.Value; service.Config.CloseOwnedLauncher = cleanup.IsOn;
        await Change("edit", editor.Text);
    }
    private static async Task ValidateScript(string text)
    {
        string command = "[Console]::InputEncoding=[Text.Encoding]::UTF8;$t=$null;$e=$null;[System.Management.Automation.Language.Parser]::ParseInput([Console]::In.ReadToEnd(),[ref]$t,[ref]$e)|Out-Null;if($e.Count){$e|ForEach-Object{$_.Message};exit 1}";
        var start = new ProcessStartInfo(SteamLaunch.PowerShellPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, StandardInputEncoding = new UTF8Encoding(false), RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(text); process.StandardInput.Close();
        await process.WaitForExitAsync(); string message = await output + await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException("脚本语法有误：\n" + message);
    }
}
#endif
