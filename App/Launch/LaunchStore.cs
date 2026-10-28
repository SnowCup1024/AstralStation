using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AstralStation.Launch;

public sealed class LaunchStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string Root { get; }
    public List<LaunchTask> Tasks { get; private set; } = [];
    public LaunchSettings Settings { get; private set; } = new();
    public List<Activity> Activities { get; } = [];
    private Dictionary<string, string> state = [];
    public string? Error { get; private set; }
    public int Revision { get; private set; }
    private readonly Dictionary<string, (long Written, long Length, bool HasRecords, string? Error)> historyIndex = [];
    public LaunchStore(string root)
    {
        Root = root;
        try
        {
            Directory.CreateDirectory(root);
            if (File.Exists(FileAt("config.json"))) Tasks = ReadTasks(FileAt("config.json"));
            if (File.Exists(FileAt("state.json"))) state = Read<Dictionary<string, string>>(FileAt("state.json"));
            if (state.Any(x => !ValidDate(x.Value))) throw new InvalidDataException("完成状态日期无效。");
            if (File.Exists(FileAt("settings.json"))) Settings = Read<LaunchSettings>(FileAt("settings.json"));
            foreach (string name in new[] { "runtime.previous.log", "runtime.log" })
                if (File.Exists(FileAt(name))) foreach (string line in File.ReadLines(FileAt(name)))
                    if (line.Length > 20 && DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) && time >= DateTime.Now.AddDays(-1) && time <= DateTime.Now) Activities.Add(new(time, line[20..]));
            Activities.Sort((a, b) => a.Time.CompareTo(b.Time));
        }
        catch (Exception ex) { Fail("数据读取失败，已停止调度。原文件不会覆盖，请检查 .bak 备份。\n" + ex.Message); }
    }
    public string FileAt(string name) => System.IO.Path.Combine(Root, name);
    public void Fail(string error) => Error = error;
    private void Writable() { if (Error is not null) throw new InvalidOperationException(Error); }
    private static T Read<T>(string file)
    {
        if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("数据文件过大。");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(file, new UTF8Encoding(false, true)), Json) ?? throw new InvalidDataException("空数据文件。");
    }
    public static List<LaunchTask> ReadTasks(string file)
    {
        var root = Read<JsonObject>(file);
        if (root["SchemaVersion"] is JsonNode version && version.GetValue<int>() > 1) throw new InvalidDataException("任务格式版本高于当前版本。");
        var array = root["Tasks"]?.AsArray() ?? throw new InvalidDataException("缺少任务数组。");
        var result = new List<LaunchTask>();
        foreach (var node in array)
        {
            if (node is not JsonObject obj || obj["Id"] is null || obj["Name"] is null || obj["Path"] is null || obj["ProcessNames"] is null) throw new InvalidDataException("任务字段缺失。");
            if (obj["ProcessNames"] is JsonValue names && names.TryGetValue<string>(out var name)) obj["ProcessNames"] = new JsonArray(JsonValue.Create(name));
            if (obj["ResetSeconds"] is null) obj["ResetSeconds"] = 0;
            var task = obj.Deserialize<LaunchTask>(Json)!; task.Validate(); result.Add(task);
        }
        if (result.Select(t => t.Id).Distinct().Count() != result.Count) throw new InvalidDataException("任务 ID 重复。");
        return result;
    }
    private static void Atomic(string file, object value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        string temporary = file + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json) + "\n");
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        if (File.Exists(file)) File.Replace(temporary, file, file + ".bak"); else File.Move(temporary, file);
    }
    public void SaveTasks(IEnumerable<LaunchTask> tasks)
    {
        Writable(); var next = tasks.Select(t => t.Clone()).ToList();
        foreach (var task in next) task.Validate();
        if (next.Select(t => t.Id).Distinct().Count() != next.Count) throw new InvalidDataException("任务 ID 重复。");
        Atomic(FileAt("config.json"), new { SchemaVersion = 1, Tasks = next }); Tasks = next;
    }
    public void SaveSettings(LaunchSettings next) { Writable(); Atomic(FileAt("settings.json"), next); Settings = next; }
    public bool Done(LaunchTask task, DateTime now) => state.GetValueOrDefault(task.Id) == task.Cycle(now);
    public void Mark(LaunchTask task, DateTime now) { Writable(); var next = new Dictionary<string, string>(state) { [task.Id] = task.Cycle(now) }; Atomic(FileAt("state.json"), next); state = next; }
    public void Clear(string? id = null) { Writable(); var next = new Dictionary<string, string>(state); if (id is null) next.Clear(); else next.Remove(id); Atomic(FileAt("state.json"), next); state = next; }
    public void Log(string text)
    {
        Writable(); var now = DateTime.Now; text = text.Replace('\r', ' ').Replace('\n', ' ');
        string file = FileAt("runtime.log");
        if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.Move(file, FileAt("runtime.previous.log"), true);
        File.AppendAllText(file, $"{now:yyyy-MM-dd HH:mm:ss} {text}\n", Encoding.UTF8);
        Activities.Add(new(now, text)); Activities.RemoveAll(a => a.Time < now.AddDays(-1));
    }
    public static bool ValidDate(string value) => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    public List<LaunchRecord> Records(string date)
    {
        if (!ValidDate(date)) throw new InvalidDataException("无效的日历日期。");
        string file = FileAt($"History/{date}.json");
        if (!File.Exists(file)) return [];
        var node = Read<JsonObject>(file);
        var result = node["Entries"]?.Deserialize<List<LaunchRecord>>(Json) ?? throw new InvalidDataException("缺少历史记录。");
        var ids = new HashSet<string>();
        foreach (var record in result)
        {
            if (record is null || string.IsNullOrWhiteSpace(record.Name) || string.IsNullOrWhiteSpace(record.Path) || !DateTime.TryParseExact(record.Time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new InvalidDataException("日历记录格式无效。");
            if (record.ResetSeconds is < 0 or > 86399 || (record.ResetSeconds is int reset ? LaunchTask.Cycle(time, reset) : time.ToString("yyyy-MM-dd")) != date) throw new InvalidDataException("日历记录归属日期无效。");
            if (!string.IsNullOrEmpty(record.Id) && !ids.Add(record.Id)) throw new InvalidDataException("日历记录 ID 重复。");
            if (!string.IsNullOrEmpty(record.EndTime) && !DateTime.TryParseExact(record.EndTime, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new InvalidDataException("退出时间无效。");
            record.Date = date;
        }
        return result;
    }
    public (List<string> Dates, List<string> Errors) HistoryDates()
    {
        var dates = new List<string>(); var errors = new List<string>(); string folder = FileAt("History");
        if (!Directory.Exists(folder)) return (dates, errors);
        var seen = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            var date = System.IO.Path.GetFileNameWithoutExtension(file);
            if (!ValidDate(date)) continue;
            seen.Add(date);
            try
            {
                var info = new FileInfo(file);
                if (!historyIndex.TryGetValue(date, out var item) || item.Error is not null || item.Written != info.LastWriteTimeUtc.Ticks || item.Length != info.Length)
                {
                    bool hasRecords = false; string? error = null;
                    try { hasRecords = Records(date).Count > 0; } catch (Exception ex) { error = ex.Message; }
                    historyIndex[date] = item = (info.LastWriteTimeUtc.Ticks, info.Length, hasRecords, error);
                }
                if (item.Error is not null) errors.Add(date + "：" + item.Error);
                else if (item.HasRecords) dates.Add(date);
            }
            catch (Exception ex) { errors.Add(date + "：" + ex.Message); }
        }
        foreach (var date in historyIndex.Keys.Where(date => !seen.Contains(date)).ToArray()) historyIndex.Remove(date);
        dates.Sort(StringComparer.Ordinal); return (dates, errors);
    }
    public string RecordLaunch(LaunchTask task, DateTime launchedAt)
    {
        Writable(); string date = task.Cycle(launchedAt); var records = Records(date); string id = Guid.NewGuid().ToString();
        records.Add(new() { Id = id, Name = task.Name, Path = task.Path, Time = launchedAt.ToString("yyyy-MM-dd HH:mm:ss"), ResetSeconds = task.ResetSeconds });
        Atomic(FileAt($"History/{date}.json"), new { Entries = records }); Revision++; return id;
    }
    public void Finish(string date, string id, DateTime ended)
    {
        Writable(); var records = Records(date); var record = records.FirstOrDefault(r => r.Id == id) ?? throw new InvalidDataException("找不到待补写退出时间的记录。");
        record.EndTime = ended.ToString("yyyy-MM-dd HH:mm:ss"); Atomic(FileAt($"History/{date}.json"), new { Entries = records }); Revision++;
    }
}
