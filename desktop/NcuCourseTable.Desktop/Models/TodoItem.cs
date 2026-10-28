using System.Globalization;
using System.Text.Json.Serialization;

namespace NcuCourseTable.Desktop.Models;

/// <summary>一条待办。DueDate 使用 yyyy-MM-dd 便于与日程数据保持一致。</summary>
public sealed class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    public string Notes { get; set; } = "";

    public string DueDate { get; set; } = "";

    /// <summary>0=普通，1=重要，2=紧急。</summary>
    public int Priority { get; set; }

    public bool Done { get; set; }

    public string CompletedAt { get; set; } = "";

    public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

    /// <summary>最后修改时间（UTC，ISO-8601）；云端合并冲突时用。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedAt { get; set; }

    [JsonIgnore]
    public DateTime? Due => ParseDue(DueDate);

    [JsonIgnore]
    public bool HasDue => Due is not null;

    [JsonIgnore]
    public bool IsOverdue
    {
        get
        {
            if (Done || Due is not { } due) return false;
            return due < DateTime.Today;
        }
    }

    [JsonIgnore]
    public string DueState
    {
        get
        {
            if (Due is not { } due) return "none";
            int days = (due - DateTime.Today).Days;
            if (Done) return "done";
            if (days < 0) return "overdue";
            if (days == 0) return "today";
            if (days <= 2) return "soon";
            return "future";
        }
    }

    [JsonIgnore]
    public string DueText
    {
        get
        {
            if (Due is not { } due) return "";
            int days = (due - DateTime.Today).Days;
            string day = days switch
            {
                0 => "今天",
                1 => "明天",
                -1 => "昨天",
                _ => $"{due.Month}月{due.Day}日",
            };
            if (!Done && days < 0)
            {
                return $"{due.Month}月{due.Day}日 · 逾期 {-days} 天";
            }
            if (!Done && days <= 2) return $"{day}截止";
            return $"{due.Month}月{due.Day}日 · {ScheduleEvent.WeekdayName(ScheduleEvent.WeekdayOf(due))}";
        }
    }

    [JsonIgnore]
    public string PriorityText => Priority switch
    {
        >= 2 => "紧急",
        1 => "重要",
        _ => "",
    };

    [JsonIgnore]
    public string PriorityLabel => Priority switch
    {
        >= 2 => "紧急",
        1 => "重要",
        _ => "普通",
    };

    [JsonIgnore]
    public string PriorityTip => $"优先级：{PriorityLabel}（点击选择）";

    [JsonIgnore]
    public bool HasPriority => Priority > 0;

    [JsonIgnore]
    public string NotesPreview
    {
        get
        {
            string t = (Notes ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length <= 42 ? t : t[..42] + "…";
        }
    }

    public void Toggle()
    {
        Done = !Done;
        CompletedAt = Done ? DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") : "";
    }

    public TodoItem Clone() => new()
    {
        Id = Id,
        Title = Title,
        Notes = Notes,
        DueDate = DueDate,
        Priority = Priority,
        Done = Done,
        CompletedAt = CompletedAt,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };

    public void CopyFrom(TodoItem from)
    {
        Title = from.Title;
        Notes = from.Notes;
        DueDate = from.DueDate;
        Priority = from.Priority;
    }

    public static string FormatDue(DateTime? date) =>
        date is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

    public static DateTime? ParseDue(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime d) ? d.Date : null;
}
