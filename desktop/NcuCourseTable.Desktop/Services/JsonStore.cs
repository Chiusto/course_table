using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using NcuCourseTable.Desktop.Models;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// 本地课表库读写（courses.json）。
/// 存储设计：单 JSON 文件 + 原子写（临时文件替换）+ 覆盖前自动备份 .bak。
/// 选 JSON 而非 SQLite：单用户、数据量小（一学期几十门课），透明、可 diff、
/// 可与 ncu_sdk 导出的 JSON 直接互换；备份即复制。
/// </summary>
public sealed class JsonStore
{
    public const string FileName = "courses.json";

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 保持中文可读
    };

    public string Dir { get; }
    public string FilePath { get; }
    public StoreData Data { get; private set; } = new();

    public JsonStore(string? dir = null)
    {
        Dir = ResolveDir(dir);
        FilePath = Path.Combine(Dir, FileName);
    }

    /// <summary>数据目录：--data 参数 &gt; NCU_COURSE_DATA 环境变量 &gt; %LocalAppData%\NcuCourseTable。</summary>
    public static string ResolveDir(string? dir)
    {
        if (!string.IsNullOrWhiteSpace(dir))
        {
            return Path.GetFullPath(dir!);
        }
        string? env = Environment.GetEnvironmentVariable("NCU_COURSE_DATA");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
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
        if (!File.Exists(FilePath))
        {
            Data = new StoreData();
            return Data;
        }
        try
        {
            string json = File.ReadAllText(FilePath);
            Data = JsonSerializer.Deserialize<StoreData>(json, JsonOpts) ?? new StoreData();
            Normalize();
        }
        catch (Exception ex)
        {
            // 损坏文件不致命：改名保留现场，回退空库
            File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            Data = new StoreData();
            System.Diagnostics.Debug.WriteLine($"courses.json 解析失败：{ex.Message}");
        }
        return Data;
    }

    public void Normalize()
    {
        Data.Version = 1;
        if (Data.TotalWeeks <= 0) Data.TotalWeeks = 20;
        if (Data.Sections.Count == 0) Data.Sections = SectionTime.Default().ToList();
        // ncu_sdk 导出的 JSON 不含学期起始日：从 termcode 推断（如 202620271 -> 2026-08-31，
        // 与 docs/API_SPEC.md 实测课表一致），用户可在数据文件里覆盖。
        if (string.IsNullOrWhiteSpace(Data.TermStart))
        {
            string? guess = GuessTermStart(Data.Termcode);
            if (guess is not null) Data.TermStart = guess;
        }
        foreach (var c in Data.Courses)
        {
            if (string.IsNullOrWhiteSpace(c.Id)) c.Id = Guid.NewGuid().ToString("N");
            if (c.Weekday < 1) c.Weekday = 1;
            if (c.Weekday > 7) c.Weekday = 7;
            if (c.StartSection < 1) c.StartSection = 1;
            if (c.EndSection < c.StartSection) c.EndSection = c.StartSection;
        }
    }

    /// <summary>
    /// termcode -> 第 1 周周一的近似值（学年第 1 学期取当年 8 月 31 日，第 2 学期取次年 2 月 24 日）。
    /// termcode 规则（ncu_sdk config.make_termcode）：前 4 位=学年起始年、末位=学期号，
    /// 如 202620271 = 2026-2027 学年第 1 学期。曾误取第 4 位（"2026**2**0271" 恒判为第 2 学期、
    /// 给出 2027-02-24），会导致“今天第几周/看哪些课”整体错位，此处以末位为准。
    /// 精确校历日期请在数据文件里用 TermStart 覆盖（登录同步后仍保留用户校准值）。
    /// </summary>
    private static string? GuessTermStart(string termcode)
    {
        if (string.IsNullOrWhiteSpace(termcode) || termcode.Length < 5 ||
            !int.TryParse(termcode.AsSpan(0, 4), out int year))
        {
            return null;
        }
        return TermDigit(termcode) switch
        {
            '1' => $"{year}-08-31",
            '2' => $"{year + 1}-02-24",
            _ => null,
        };
    }

    /// <summary>取 termcode 的学期号字符：规范 9 位时在末位（202620271 -> '1'）。</summary>
    private static char TermDigit(string termcode)
    {
        char last = termcode[^1];
        if (last is '1' or '2') return last;
        return termcode.Length > 4 && termcode[4] is '1' or '2' ? termcode[4] : '\0';
    }

    /// <summary>原子保存：先写 .tmp 再替换；原文件备份为 courses.json.bak。</summary>
    public void Save()
    {
        Directory.CreateDirectory(Dir);
        Normalize();
        string json = JsonSerializer.Serialize(Data, JsonOpts);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
        if (File.Exists(FilePath))
        {
            string bak = FilePath + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(FilePath, bak);
        }
        File.Move(tmp, FilePath);
    }

    // ------------------------------------------------------------------ 导入
    /// <summary>
    /// 导入 ncu_sdk 导出的 JSON（Course.to_dict 数组，或本程序自己的 courses 数组）。
    /// 语义：整学期替换 —— 覆盖当前学期已有课程，避免重复同步产生重复数据。
    /// </summary>
    public int ImportNcuJson(string file, string? forceTerm = null)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        JsonElement root = doc.RootElement;
        JsonElement arr = root.ValueKind == JsonValueKind.Array
            ? root
            : (root.TryGetProperty("courses", out var cs) ? cs : throw new FormatException("不是课程数组"));

        // 先扫描一遍确定目标学期（参数 > 文件首条 termcode > 当前库）
        string term = forceTerm ?? "";
        if (term.Length == 0 && arr.GetArrayLength() > 0 &&
            arr[0].TryGetProperty("termcode", out var firstT) && firstT.ValueKind != JsonValueKind.Null)
        {
            term = firstT.ToString().Trim();
        }
        if (term.Length == 0) term = Data.Termcode;

        var imported = new List<Course>();
        foreach (JsonElement el in arr.EnumerateArray())
        {
            Course c = MapDto(el, term);
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            imported.Add(c);
        }
        if (imported.Count == 0)
        {
            return 0;
        }

        // 整学期替换
        Data.Courses.RemoveAll(c => c.Termcode == term);
        if (term.Length == 0)
        {
            Data.Courses.Clear();
        }
        Data.Courses.AddRange(imported);
        if (Data.Termcode.Length == 0) Data.Termcode = term;
        if (Data.TermName.Length == 0 && imported[0].Termcode.Length > 0)
        {
            Data.TermName = FormatTermName(imported[0].Termcode);
        }
        Save();
        return imported.Count;
    }

    /// <summary>
    /// 202620271 -> "2026-2027学年第1学期"（展示用，可被数据文件 TermName 覆盖）。
    /// 与 GuessTermStart 同一规则：9 位 termcode 的学期号在末位。
    /// </summary>
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

    /// <summary>宽松字段映射：兼容 snake_case（ncu_sdk to_dict）与 PascalCase 两种命名。</summary>
    private static Course MapDto(JsonElement el, string fallbackTerm)
    {
        string Str(string snake, string pascal)
        {
            foreach (var key in new[] { snake, pascal })
            {
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) &&
                    v.ValueKind != JsonValueKind.Null)
                {
                    return v.ToString().Trim();
                }
            }
            return "";
        }

        int Num(string snake, string pascal, int dflt)
        {
            foreach (var key in new[] { snake, pascal })
            {
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) &&
                    v.ValueKind != JsonValueKind.Null && v.TryGetInt32(out int i))
                {
                    return i;
                }
            }
            return dflt;
        }

        var c = new Course
        {
            Termcode = Str("termcode", "Termcode") is { Length: > 0 } t ? t : fallbackTerm,
            Name = Str("name", "Name"),
            Teacher = Str("teacher", "Teacher"),
            Room = Str("room", "Room"),
            Raw = Str("raw", "Raw"),
        };

        if (Str("room", "Room").Length == 0)
        {
            c.Room = Str("location", "Location"); // 兼容其他导出方
        }
        if (Str("weeks_raw", "WeeksRaw").Length > 0)
        {
            c.WeeksRaw = Str("weeks_raw", "WeeksRaw");
        }
        else if (Str("weeks", "Weeks").Length > 0)
        {
            c.WeeksRaw = Str("weeks", "Weeks");
        }

        c.Weekday = Math.Clamp(Num("weekday", "Weekday", 1), 1, 7);
        int s = Math.Max(1, Num("start_section", "StartSection", 1));
        int e = Math.Max(1, Num("end_section", "EndSection", s));
        if (e < s) (s, e) = (e, s);
        c.StartSection = s;
        c.EndSection = e;
        return c;
    }

    // ------------------------------------------------------------------ 演示数据
    /// <summary>
    /// 内置演示课表：与 ncu_sdk/demo.py 的“实测 2026-2027-1 课表”逐条一致
    /// （来源 docs/API_SPEC.md 4.2），用于首次运行 / 无网环境。
    /// </summary>
    public void FillDemo()
    {
        Data = new StoreData
        {
            Termcode = "202620271",
            TermName = "2026-2027学年第1学期",
            TermStart = "2026-08-31", // 第 1 周周一
            TotalWeeks = 20,
            Sections = SectionTime.Default().ToList(),
            Links = new Dictionary<string, string>
            {
                ["portal"] = "https://portal.ncu.edu.cn",
                ["教务查询"] = "https://gms.ncu.edu.cn/",
            },
            Courses = new List<Course>
            {
                Demo("机器学习", "胡书凡", "前湖北校区研究生院316", 1, 6, 8, "1-11周"),
                Demo("高级计算机系统结构", "张宇成", "前湖北校区研究生院215", 3, 3, 4, "1-16周"),
                Demo("数据科学与工程", "王洋洋", "前湖北校区研究生院316", 3, 8, 10, "1-16周"),
                Demo("最优化", "肖艳阳", "前湖北校区研究生院316", 3, 11, 13, "1-16周"),
                Demo("自然辩证法", "康琳", "前湖北校区研究生院316", 4, 4, 4, "1-8周"),
                Demo("组合数学", "幸玮", "前湖北校区研究生院316", 5, 3, 5, "5-15周"),
                Demo("工程伦理", "刘韬", "基础实验大楼A106", 6, 3, 5, "6-6周"),
                Demo("工程伦理", "刘韬", "基础实验大楼A106", 7, 6, 10, "6-6周"),
            },
        };
        Save();
    }

    private static Course Demo(string name, string teacher, string room,
        int weekday, int start, int end, string weeks) => new()
    {
        Termcode = "202620271",
        Name = name,
        Teacher = teacher,
        Room = room,
        Weekday = weekday,
        StartSection = start,
        EndSection = end,
        WeeksRaw = weeks,
    };
}
