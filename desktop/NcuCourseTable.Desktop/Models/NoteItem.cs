using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NcuCourseTable.Desktop.Models;

/// <summary>一条笔记（flomo 风格便签）。正文中的 #标签 在展示时解析，不单独存字段。</summary>
public sealed class NoteItem
{
    private static readonly Regex TagRe = new(@"(?<!\S)#([^\s#]+)", RegexOptions.Compiled);

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Content { get; set; } = "";

    public bool Pinned { get; set; }

    /// <summary>卡片配色：空为默认白，其余为 yellow/green/blue/pink/purple。</summary>
    public string Color { get; set; } = "";

    public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

    /// <summary>最后修改时间（UTC，ISO-8601）；云端合并冲突时用。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedAt { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> Tags => ParseTags(Content);

    [JsonIgnore]
    public string TimeText
    {
        get
        {
            if (!DateTime.TryParseExact(CreatedAt, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime at))
            {
                return "";
            }
            return at.Year == DateTime.Today.Year
                ? at.ToString("M月d日 HH:mm", CultureInfo.InvariantCulture)
                : at.ToString("yyyy年M月d日", CultureInfo.InvariantCulture);
        }
    }

    public static IReadOnlyList<string> ParseTags(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return Array.Empty<string>();
        var seen = new List<string>();
        foreach (Match m in TagRe.Matches(content))
        {
            string tag = m.Groups[1].Value.Trim();
            if (tag.Length == 0) continue;
            if (!seen.Contains(tag, StringComparer.OrdinalIgnoreCase)) seen.Add(tag);
        }
        return seen;
    }

    public void Touch() =>
        UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public NoteItem Clone() => new()
    {
        Id = Id,
        Content = Content,
        Pinned = Pinned,
        Color = Color,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };

    public void CopyFrom(NoteItem from)
    {
        Content = from.Content;
        Pinned = from.Pinned;
        Color = from.Color;
        Touch();
    }
}
