using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;

namespace AstralStation.Launch;

public sealed class LaunchTask : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> ProcessNames { get; set; } = [];
    public int ResetSeconds { get; set; } = 14400;
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public int DelaySeconds { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 15;
    private int order;
    [JsonIgnore] public int Order { get => order; set { if (order == value) return; order = value; PropertyChanged?.Invoke(this, new(nameof(Order))); PropertyChanged?.Invoke(this, new(nameof(SeparatorOpacity))); } }
    [JsonIgnore] public double SeparatorOpacity => Order > 1 ? 1 : 0;
    private string status = "";
    [JsonIgnore] public string Status { get => status; set { if (status == value) return; status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    private string completion = "";
    [JsonIgnore] public string Completion { get => completion; set { if (completion == value) return; completion = value; PropertyChanged?.Invoke(this, new(nameof(Completion))); } }
    [JsonIgnore] public string ResetLabel => $"{TimeSpan.FromSeconds(ResetSeconds):hh\\:mm}";
    [JsonIgnore] public string Detail => $"{ResetLabel}重置  启动前等待 {DelaySeconds} 秒";
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Name;
    public LaunchTask Clone() => new() { Id = Id, Name = Name, Path = Path, ProcessNames = [.. ProcessNames], ResetSeconds = ResetSeconds, Arguments = Arguments, WorkingDirectory = WorkingDirectory, DelaySeconds = DelaySeconds, TimeoutSeconds = TimeoutSeconds };
    public static string Normalize(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
    public string Cycle(DateTime time) => Cycle(time, ResetSeconds);
    public static string Cycle(DateTime time, int reset) => (time.TimeOfDay.TotalSeconds < reset ? time.Date.AddDays(-1) : time.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public void Validate()
    {
        if (Id is null || Name is null || Path is null || ProcessNames is null || Arguments is null || WorkingDirectory is null) throw new InvalidDataException("任务字段不能为空。");
        Id = Id.Trim(); Name = Name.Trim(); Path = Path.Trim(); WorkingDirectory = WorkingDirectory.Trim();
        if (Id.Length == 0 || Name.Length == 0 || Path.Length == 0) throw new InvalidDataException("任务名称、路径和 ID 不能为空。");
        if (ResetSeconds is < 0 or > 86399 || DelaySeconds is < 0 or > 3600 || TimeoutSeconds is < 1 or > 600) throw new InvalidDataException("重置时间或等待时间超出范围。");
        if (Arguments.Length > 16000 || Arguments.Contains('\r') || Arguments.Contains('\n')) throw new InvalidDataException("启动参数必须是单行且不超过 16000 字符。");
        if (ProcessNames.Any(p => p is null)) throw new InvalidDataException("进程名称无效。");
        ProcessNames = ProcessNames.Select(Normalize).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
public sealed class LaunchSettings
{
    public string Theme { get; set; } = "Default";
    public bool AutoClose { get; set; }
    public bool Tray { get; set; }
    public bool CloseToTray { get; set; }
    public LaunchSettings Clone() => (LaunchSettings)MemberwiseClone();
}
public sealed class LaunchRecord
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Time { get; set; } = "";
    public string Id { get; set; } = "";
    public string EndTime { get; set; } = "";
    public int? ResetSeconds { get; set; }
    [JsonIgnore] public string Date { get; set; } = "";
    [JsonIgnore] public string Source { get; set; } = "启动队列";
    [JsonIgnore] public string Note { get; set; } = "";
    [JsonIgnore] public string GameKey { get; set; } = "";
    [JsonIgnore] public DateTime? LastObservedAt { get; set; }
    [JsonIgnore] public string Summary => Format(Time) + (string.IsNullOrEmpty(EndTime) ? "" : " — " + Format(EndTime)) + (ResetSeconds is int reset ? $"（{TimeSpan.FromSeconds(reset):hh\\:mm}重置）" : "");
    [JsonIgnore] public string CopyText => $"{Name}\n{Path}\n{Date}\n{Summary}\n{Source}" + (Note.Length == 0 ? "" : "\n" + Note);
    private string Format(string stamp)
    {
        var date = DateTime.ParseExact(stamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        int days = (date.Date - DateTime.ParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)).Days;
        return days == 0 ? date.ToString("HH:mm:ss") : days == 1 ? "次日 " + date.ToString("HH:mm:ss") : stamp;
    }
    internal static List<LaunchRecord> Merge(IEnumerable<LaunchRecord> records)
    {
        static DateTime Stamp(string value) => DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        static string Key(LaunchRecord record)
        {
            if (record.GameKey.Length > 0) return record.GameKey;
            try { return System.IO.Path.GetFullPath(record.Path); } catch { return record.Path; }
        }
        var result = new List<LaunchRecord>();
        foreach (var game in records.GroupBy(Key, StringComparer.OrdinalIgnoreCase))
        {
            var parts = new List<LaunchRecord>(); DateTime end = default;
            void Flush()
            {
                if (parts.Count == 0) return;
                var first = parts[0];
                bool running = parts.Any(p => p.EndTime.Length == 0 && p.Note == "运行中");
                var notes = parts.Select(p => p.Note).Where(n => n.Length > 0 && n != "运行中").Distinct().ToList();
                if (parts.Count > 1) notes.Add($"已合并 {parts.Count} 段记录");
                if (running) notes.Add("运行中");
                result.Add(new LaunchRecord
                {
                    Id = first.Id, Name = first.Name, Path = first.Path, Time = first.Time, Date = first.Date, ResetSeconds = first.ResetSeconds,
                    EndTime = running || parts.All(p => p.EndTime.Length == 0) ? "" : end.ToString("yyyy-MM-dd HH:mm:ss"),
                    Source = string.Join("、", parts.Select(p => p.Source).Distinct()), Note = string.Join(" · ", notes), GameKey = game.Key, LastObservedAt = end
                });
                parts.Clear();
            }
            foreach (var record in game.OrderBy(p => p.Time, StringComparer.Ordinal).ThenBy(p => p.Date, StringComparer.Ordinal))
            {
                DateTime start = Stamp(record.Time);
                if (parts.Count > 0 && start - end >= TimeSpan.FromMinutes(5)) Flush();
                DateTime observed = record.EndTime.Length > 0 ? Stamp(record.EndTime) : record.LastObservedAt ?? start;
                end = parts.Count == 0 || observed > end ? observed : end;
                parts.Add(record);
            }
            Flush();
        }
        return result.OrderBy(p => p.Time, StringComparer.Ordinal).ToList();
    }
}
public sealed record Activity(DateTime Time, string Message)
{
    public override string ToString() => $"{Time:MM-dd HH:mm:ss}  {Message}";
}
