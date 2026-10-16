using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;

namespace AstralStation.Modules.Launch;

public sealed class LaunchTask : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> ProcessNames { get; set; } = [];
    public int ResetSeconds { get; set; } = 14400;
    public bool Enabled { get; set; } = true;
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public int DelaySeconds { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 15;
    private string status = "";
    [JsonIgnore] public string Status { get => status; set { if (status == value) return; status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    [JsonIgnore] public string Detail => $"{(Enabled ? "自动" : "仅手动")} · {TimeSpan.FromSeconds(ResetSeconds):hh\\:mm} 重置 · 等待 {DelaySeconds} 秒";
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Name;
    public LaunchTask Clone() => new() { Id = Id, Name = Name, Path = Path, ProcessNames = [.. ProcessNames], ResetSeconds = ResetSeconds, Enabled = Enabled, Arguments = Arguments, WorkingDirectory = WorkingDirectory, DelaySeconds = DelaySeconds, TimeoutSeconds = TimeoutSeconds };
    public static string Normalize(string name) => name.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Trim()[..^4] : name.Trim();
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
    [JsonIgnore] public string Summary => Format(Time) + (string.IsNullOrEmpty(EndTime) ? "" : " — " + Format(EndTime)) + (ResetSeconds is int reset ? $"\n重置时间 {TimeSpan.FromSeconds(reset):hh\\:mm}" : "");
    [JsonIgnore] public string CopyText => $"{Name}\n{Path}\n{Date}\n{Summary}";
    private string Format(string stamp)
    {
        var date = DateTime.ParseExact(stamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        int days = (date.Date - DateTime.ParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)).Days;
        return days == 0 ? date.ToString("HH:mm:ss") : days == 1 ? "次日 " + date.ToString("HH:mm:ss") : stamp;
    }
}
public sealed record Activity(DateTime Time, string Message)
{
    public override string ToString() => $"{Time:MM-dd HH:mm:ss}  {Message}";
}
