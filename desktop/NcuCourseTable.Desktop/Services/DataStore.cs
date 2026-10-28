using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using NcuCourseTable.Desktop.Models;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// 本地时间表数据读写（schedule.json）。
/// 单 JSON + 原子写 + 覆盖前备份 .bak；旧版 courses.json 首次载入时自动迁移为 events，
/// 原文件保留作为备份，后续读写只使用 schedule.json。
/// </summary>
public sealed class DataStore
{
    public const string FileName = "schedule.json";
    public const string LegacyFileName = "courses.json";
    public const string LegacySourceId = "ncu";

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Dir { get; }
    public string FilePath { get; }
    public string LegacyFilePath { get; }
    public StoreData Data { get; private set; } = new();

    /// <summary>本机保存（含插件导入）后触发，供自动同步监听。</summary>
    public event Action? Saved;

    /// <summary>本进程最近一次写入磁盘的 UTC 时间（Save / Replace），供文件监视忽略自身写入。</summary>
    public DateTime LastProgrammaticWriteUtc { get; private set; }

    private Dictionary<string, string> _fingerprints = new(StringComparer.Ordinal);

    public DataStore(string? dir = null)
    {
        Dir = ResolveDir(dir);
        FilePath = Path.Combine(Dir, FileName);
        LegacyFilePath = Path.Combine(Dir, LegacyFileName);
    }

    public static string ResolveDir(string? dir)
    {
        if (!string.IsNullOrWhiteSpace(dir))
        {
            return Path.GetFullPath(dir!);
        }
        string? env = Environment.GetEnvironmentVariable("TIMETABLE_DATA")
            ?? Environment.GetEnvironmentVariable("NCU_COURSE_DATA");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
        }
        string portable = Path.Combine(AppContext.BaseDirectory, "data");
        if (Directory.Exists(portable))
        {
            return portable;
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NcuCourseTable");
    }

    public bool FileExists => File.Exists(FilePath);

    public IReadOnlyDictionary<int, SectionTime> SectionMap => Data.Sections.Count > 0
        ? Data.Sections.ToDictionary(s => s.Index)
        : SectionTime.DefaultMap();

    public StoreData Load()
    {
        Directory.CreateDirectory(Dir);

        string source = File.Exists(FilePath) ? FilePath
            : File.Exists(LegacyFilePath) ? LegacyFilePath
            : "";
        if (source.Length == 0)
        {
            Data = NewData();
            _fingerprints.Clear();
            return Data;
        }

        try
        {
            Data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(source), JsonOpts)
                ?? new StoreData();
            bool migrated = MigrateLegacyCourses();
            Normalize();
            _fingerprints = SnapshotFingerprints();
            if (migrated || source == LegacyFilePath)
            {
                Save();
            }
        }
        catch (Exception ex)
        {
            File.Copy(source, source + ".bad", overwrite: true);
            Data = NewData();
            _fingerprints.Clear();
            System.Diagnostics.Debug.WriteLine($"时间表数据解析失败：{ex.Message}");
        }
        return Data;
    }

    private static StoreData NewData() => new()
    {
        TermStart = MondayOfWeek(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    private bool MigrateLegacyCourses()
    {
        if (Data.LegacyCourses is not { Count: > 0 } legacy) return false;

        foreach (LegacyCourse course in legacy)
        {
            if (string.IsNullOrWhiteSpace(course.Name)) continue;
            Data.Events.Add(new ScheduleEvent
            {
                Id = string.IsNullOrWhiteSpace(course.Id) ? Guid.NewGuid().ToString("N") : course.Id,
                Title = course.Name.Trim(),
                Category = "课程",
                Teacher = course.Teacher ?? "",
                Location = course.Room ?? "",
                Source = LegacySourceId,
                Weekday = Math.Clamp(course.Weekday, 1, 7),
                StartSection = Math.Max(1, course.StartSection),
                EndSection = Math.Max(Math.Max(1, course.StartSection), course.EndSection),
                WeeksRaw = string.IsNullOrWhiteSpace(course.WeeksRaw) ? "1-16周" : course.WeeksRaw,
                Raw = course.Raw,
            });
            if (Data.Termcode.Length == 0 && course.Termcode.Length > 0)
            {
                Data.Termcode = course.Termcode;
            }
        }
        Data.LegacyCourses = null;
        return true;
    }

    public void Normalize()
    {
        Data.Version = 2;
        if (Data.TotalWeeks is < 1 or > 60) Data.TotalWeeks = 20;
        if (Data.Sections.Count == 0) Data.Sections = SectionTime.Default().ToList();
        if (string.IsNullOrWhiteSpace(Data.TermStart) && !string.IsNullOrWhiteSpace(Data.Termcode))
        {
            string? guess = GuessTermStart(Data.Termcode);
            if (guess is not null) Data.TermStart = guess;
        }
        if (string.IsNullOrWhiteSpace(Data.TermName) && !string.IsNullOrWhiteSpace(Data.Termcode))
        {
            Data.TermName = FormatTermName(Data.Termcode);
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ScheduleEvent e in Data.Events)
        {
            if (string.IsNullOrWhiteSpace(e.Id) || !ids.Add(e.Id))
            {
                e.Id = Guid.NewGuid().ToString("N");
                ids.Add(e.Id);
            }
            if (string.IsNullOrWhiteSpace(e.Category)) e.Category = EventCategories.Default;
            if (string.IsNullOrWhiteSpace(e.Title)) e.Title = "未命名日程";
            e.Weekday = Math.Clamp(e.Weekday, 1, 7);
            e.StartSection = Math.Clamp(e.StartSection, 1, 30);
            e.EndSection = Math.Clamp(e.EndSection, e.StartSection, 30);
            if (string.IsNullOrWhiteSpace(e.WeeksRaw)) e.WeeksRaw = $"1-{Data.TotalWeeks}周";
            if (string.IsNullOrWhiteSpace(e.Source)) e.Source = "local";
            if (DateTime.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime d))
            {
                e.Date = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                e.Weekday = ScheduleEvent.WeekdayOf(d);
            }
            else if (e.Date is not null)
            {
                e.Date = null;
            }
        }

        ids.Clear();
        foreach (TodoItem todo in Data.Todos)
        {
            if (string.IsNullOrWhiteSpace(todo.Id) || !ids.Add(todo.Id))
            {
                todo.Id = Guid.NewGuid().ToString("N");
                ids.Add(todo.Id);
            }
            if (string.IsNullOrWhiteSpace(todo.Title)) todo.Title = "未命名待办";
            todo.Priority = Math.Clamp(todo.Priority, 0, 2);
            if (DateTime.TryParseExact(todo.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime due))
            {
                todo.DueDate = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            else
            {
                todo.DueDate = "";
            }
        }

        Data.Notes.RemoveAll(n => string.IsNullOrWhiteSpace(n.Content));
        ids.Clear();
        foreach (NoteItem note in Data.Notes)
        {
            if (string.IsNullOrWhiteSpace(note.Id) || !ids.Add(note.Id))
            {
                note.Id = Guid.NewGuid().ToString("N");
                ids.Add(note.Id);
            }
            note.Content = note.Content.Trim();
        }
    }

    /// <summary>原子保存：先写 .tmp 再替换；原文件备份为 schedule.json.bak。</summary>
    public void Save()
    {
        Normalize();
        StampChangedItems();
        WriteToDisk(JsonSerializer.Serialize(Data, JsonOpts));
        _fingerprints = SnapshotFingerprints();
        Saved?.Invoke();
    }

    /// <summary>
    /// 用云端/三方合并后的数据整体替换本地。不触发 <see cref="Saved"/>，
    /// 避免同步自身写入再次触发自动上传。
    /// </summary>
    public void Replace(StoreData data)
    {
        Data = data;
        MigrateLegacyCourses();
        Normalize();
        _fingerprints = SnapshotFingerprints();
        LastProgrammaticWriteUtc = DateTime.UtcNow;
        WriteToDisk(JsonSerializer.Serialize(Data, JsonOpts));
    }

    private void WriteToDisk(string json)
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
        // 替换前后各记一次时间：文件监视事件在重命名时产生、可能在写盘期间或之后才送达，
        // 都应按「本进程写入」忽略（界面刷新走 Saved 事件）。
        LastProgrammaticWriteUtc = DateTime.UtcNow;
        if (File.Exists(FilePath))
        {
            string bak = FilePath + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(FilePath, bak);
        }
        File.Move(tmp, FilePath);
        LastProgrammaticWriteUtc = DateTime.UtcNow;
    }

    /// <summary>只为本机实际改动或新增的条目写入 UpdatedAt；载入未改动的旧数据不打标。</summary>
    private void StampChangedItems()
    {
        string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        foreach (ScheduleEvent e in Data.Events)
        {
            if (NeedsStamp("e:" + e.Id, e)) e.UpdatedAt = now;
        }
        foreach (TodoItem t in Data.Todos)
        {
            if (NeedsStamp("t:" + t.Id, t)) t.UpdatedAt = now;
        }
        foreach (NoteItem n in Data.Notes)
        {
            if (NeedsStamp("n:" + n.Id, n)) n.UpdatedAt = now;
        }
    }

    private bool NeedsStamp(string key, object item) =>
        !_fingerprints.TryGetValue(key, out string? old) ||
        !string.Equals(old, Fingerprint(item), StringComparison.Ordinal);

    private Dictionary<string, string> SnapshotFingerprints()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ScheduleEvent e in Data.Events) map["e:" + e.Id] = Fingerprint(e);
        foreach (TodoItem t in Data.Todos) map["t:" + t.Id] = Fingerprint(t);
        foreach (NoteItem n in Data.Notes) map["n:" + n.Id] = Fingerprint(n);
        return map;
    }

    /// <summary>条目内容指纹（忽略 UpdatedAt），用于改动检测与三方合并比较。</summary>
    public static string Fingerprint(object item)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(item, JsonOpts);
        if (node is JsonObject obj) obj.Remove("UpdatedAt");
        return node?.ToJsonString() ?? "";
    }

    /// <summary>
    /// termcode -> 第 1 周周一的近似值（第 1 学期 8 月 31 日、第 2 学期次年 2 月 24 日所在周的周一）。
    /// 精确校历日期可在设置里覆盖。
    /// </summary>
    public static string? GuessTermStart(string termcode)
    {
        if (string.IsNullOrWhiteSpace(termcode) || termcode.Length < 5 ||
            !int.TryParse(termcode.AsSpan(0, 4), out int year))
        {
            return null;
        }
        DateTime approximate = TermDigit(termcode) switch
        {
            '1' => new DateTime(year, 8, 31),
            '2' => new DateTime(year + 1, 2, 24),
            _ => default,
        };
        return approximate == default
            ? null
            : MondayOfWeek(approximate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static char TermDigit(string termcode)
    {
        char last = termcode[^1];
        if (last is '1' or '2') return last;
        return termcode.Length > 4 && termcode[4] is '1' or '2' ? termcode[4] : '\0';
    }

    /// <summary>202620271 -> "2026-2027学年第1学期"。</summary>
    public static string FormatTermName(string termcode)
    {
        termcode = (termcode ?? "").Trim();
        if (termcode.Length >= 5 && int.TryParse(termcode.AsSpan(0, 4), out int y))
        {
            char term = TermDigit(termcode);
            if (term is '1' or '2')
            {
                return $"{y}-{y + 1}学年第{term}学期";
            }
        }
        return termcode.Length > 0 ? $"学期 {termcode}" : "未命名学期";
    }

    public static DateTime MondayOfWeek(DateTime date)
    {
        int diff = ((int)date.DayOfWeek + 6) % 7;
        return date.Date.AddDays(-diff);
    }
}
