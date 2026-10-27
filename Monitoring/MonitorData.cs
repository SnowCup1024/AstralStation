using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AstralStation.Monitoring;

public sealed class MonitorGame
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || string.IsNullOrWhiteSpace(Path) || !System.IO.Path.IsPathFullyQualified(Path) || !Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("监测路径必须是完整的 EXE 路径。");
        Path = System.IO.Path.GetFullPath(Path);
        ProcessName = ProcessName?.Trim() ?? "";
        if (ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ProcessName = ProcessName[..^4];
        if (ProcessName.Length is 0 or > 260 || ProcessName.IndexOfAny(['\\', '/', ':', '*', '?', '"', '\r', '\n']) >= 0)
            throw new InvalidDataException("请填写有效的进程名称，不含目录。");
    }
}
public sealed class MonitorConfig
{
    public int Schema { get; set; } = 1;
    public bool Enabled { get; set; }
    public List<MonitorGame> Games { get; set; } = [];
    public void Validate()
    {
        if (Schema != 1 || Games is null || Games.Count > 256) throw new InvalidDataException("不支持的监测清单格式或条目过多。");
        foreach (var game in Games) { if (game is null) throw new InvalidDataException("监测条目为空。"); game.Validate(); }
        if (Games.Select(g => g.Id).Distinct().Count() != Games.Count || Games.Select(g => g.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Games.Count)
            throw new InvalidDataException("监测条目重复。");
    }
}
public sealed class MonitorRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MonitorGame Game { get; set; } = new();
    public int ProcessId { get; set; }
    public long CreatedUtcTicks { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string EndReason { get; set; } = "";
}
public sealed record MonitorStatus(string Version, int ProcessId, DateTimeOffset UpdatedAt, int ActiveCount, string? Error);

public static class MonitorData
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string DefaultRoot => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AstralStation", "Launch");
    public static string DirectoryAt(string root) => System.IO.Path.Combine(root, "Monitor");
    public static string ConfigPath(string root) => System.IO.Path.Combine(DirectoryAt(root), "config.json");
    public static string Identity(string root) => "AstralStation.Monitor-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(root).TrimEnd('\\', '/').ToUpperInvariant())))[..20];
    public static MonitorConfig ReadConfig(string root)
    {
        var result = File.Exists(ConfigPath(root)) ? Read<MonitorConfig>(ConfigPath(root)) : new();
        result.Validate(); return result;
    }
    public static void SaveConfig(string root, MonitorConfig config) { config.Validate(); Atomic(ConfigPath(root), config); }
    public static T Read<T>(string file)
    {
        if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("监测数据文件过大。");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(file, new UTF8Encoding(false, true)), Json) ?? throw new InvalidDataException("监测数据为空。");
    }
    public static void Atomic(string file, object value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json); stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(file)) File.Replace(temporary, file, file + ".bak"); else File.Move(temporary, file);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string RecordPath(string root, MonitorRecord record)
    {
        if (!Guid.TryParseExact(record.Id, "N", out _)) throw new InvalidDataException("监测记录 ID 无效。");
        return System.IO.Path.Combine(DirectoryAt(root), "History", record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd"), record.Id + ".json");
    }
    public static List<string> HistoryDates(string root)
    {
        string folder = System.IO.Path.Combine(DirectoryAt(root), "History");
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateDirectories(folder).Select(System.IO.Path.GetFileName)
            .Where(name => DateTime.TryParseExact(name, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            .Select(name => name!).Order(StringComparer.Ordinal).ToList();
    }
    public static (List<MonitorRecord> Records, List<string> Errors) Records(string root, string date)
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) throw new InvalidDataException("无效的监测记录日期。");
        var records = new List<MonitorRecord>(); var errors = new List<string>();
        string folder = System.IO.Path.Combine(DirectoryAt(root), "History", date);
        if (!Directory.Exists(folder)) return (records, errors);
        foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                var record = Read<MonitorRecord>(file); record.Game.Validate();
                if (!string.Equals(RecordPath(root, record), System.IO.Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)
                    || record.ProcessId <= 0 || record.CreatedUtcTicks <= 0 || record.EndReason is null || record.StartedAt == default || record.LastSeenAt < record.StartedAt || record.EndedAt < record.StartedAt)
                    throw new InvalidDataException("记录身份、日期或起止时间无效。");
                records.Add(record);
            }
            catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(file) + "：" + ex.Message); }
        }
        return (records.OrderBy(r => r.StartedAt).ToList(), errors);
    }
}
