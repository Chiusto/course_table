using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using NcuCourseTable.Desktop.Models;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// 本地 HTTP API：允许外部程序 / 脚本向时间表添加、查询、修改、删除日程。
/// 安全边界与插件一致：只允许操作 Source == "api" 的日程；
/// 手动创建（local，即内置/手动日程）与插件导入的日程一律不可通过本 API 修改。
/// 仅监听 127.0.0.1，不对外网开放。
/// </summary>
public sealed class ApiServer : IDisposable
{
    /// <summary>API 写入日程的固定 Source 标记。</summary>
    public const string ApiSource = "api";

    public const int DefaultPort = 58970;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly DataStore _store;
    private readonly Action _refresh;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public int Port { get; private set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    /// <summary>环境变量 TIMETABLE_API_DISABLED=1/true 时禁用 API。</summary>
    public static bool IsDisabled() =>
        Environment.GetEnvironmentVariable("TIMETABLE_API_DISABLED") is { } v &&
        v is "1" or "true" or "TRUE";

    public ApiServer(DataStore store, Action refresh)
    {
        _store = store;
        _refresh = refresh;
        Port = ResolvePort();
        _listener.Prefixes.Add(BaseUrl);
    }

    /// <summary>开始监听。默认端口被占用时自动尝试后续端口（显式指定 TIMETABLE_API_PORT 则不回退）。</summary>
    public void Start()
    {
        bool fixedPort = Environment.GetEnvironmentVariable("TIMETABLE_API_PORT") is { Length: > 0 };
        int attempts = fixedPort ? 1 : 10;
        for (int i = 0; i < attempts; i++)
        {
            if (i > 0) Rebind(Port + i);
            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException)
            {
                if (fixedPort) throw;
                if (i == attempts - 1)
                {
                    throw new InvalidOperationException(
                        $"无法启动 API：端口 {Port}-{Port + attempts - 1} 均被占用。可用 TIMETABLE_API_PORT 指定端口。");
                }
            }
        }
        _loop = Task.Run(ServeLoop);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Close();
        }
        catch
        {
            // 关闭阶段的异常忽略
        }
        _cts.Dispose();
    }

    // ---------------------------------------------------------------- 内部实现

    private static int ResolvePort()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("TIMETABLE_API_PORT"),
                NumberStyles.None, CultureInfo.InvariantCulture, out int port) &&
            port is > 0 and < 65536)
        {
            return port;
        }
        return DefaultPort;
    }

    private void Rebind(int port)
    {
        _listener.Prefixes.Clear();
        Port = port;
        _listener.Prefixes.Add(BaseUrl);
    }

    private async Task ServeLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return; // 监听器已关闭
            }
            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            await Dispatch(ctx);
        }
        catch (Exception ex)
        {
            await WriteJson(ctx, 500, new { ok = false, error = "服务器错误：" + ex.Message });
        }
    }

    private async Task Dispatch(HttpListenerContext ctx)
    {
        string method = ctx.Request.HttpMethod.ToUpperInvariant();
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        ctx.Response.Headers.Set("Cache-Control", "no-store");

        switch (method)
        {
            case "GET" when path == "/api/health":
                await WriteJson(ctx, 200, new { ok = true, service = "timetable-api", source = ApiSource });
                return;

            case "GET" when path == "/api/events":
                await ListEvents(ctx);
                return;

            case "POST" when path == "/api/events":
                await AddEvents(ctx);
                return;

            case "PUT" when path.StartsWith("/api/events/", StringComparison.Ordinal):
                await UpdateEvent(ctx, path["/api/events/".Length..]);
                return;

            case "DELETE" when path.StartsWith("/api/events/", StringComparison.Ordinal):
                await DeleteEvent(ctx, path["/api/events/".Length..]);
                return;

            default:
                await WriteJson(ctx, 404, new { ok = false, error = "接口不存在" });
                return;
        }
    }

    private async Task ListEvents(HttpListenerContext ctx)
    {
        List<ScheduleEvent>? events = null;
        RunOnUi(() =>
        {
            events = _store.Data.Events
                .Where(e => string.Equals(e.Source, ApiSource, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Weekday)
                .ThenBy(e => e.StartSection)
                .ToList();
        });
        await WriteJson(ctx, 200, new { ok = true, count = events?.Count ?? 0, events });
    }

    private async Task AddEvents(HttpListenerContext ctx)
    {
        string body = await ReadBodyAsync(ctx);
        List<ApiEventItem> items;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.Deserialize<List<ApiEventItem>>(JsonOpts) ?? new List<ApiEventItem>()
                : new List<ApiEventItem> { doc.RootElement.Deserialize<ApiEventItem>(JsonOpts) ?? new ApiEventItem() };
        }
        catch (JsonException ex)
        {
            await WriteJson(ctx, 400, new { ok = false, error = "JSON 解析失败：" + ex.Message });
            return;
        }

        foreach (ApiEventItem item in items)
        {
            if (Validate(item) is { } error)
            {
                await WriteJson(ctx, 400, new { ok = false, error, item = item.Title });
                return;
            }
        }
        if (items.Count == 0)
        {
            await WriteJson(ctx, 400, new { ok = false, error = "请求体为空" });
            return;
        }

        var created = new List<ScheduleEvent>(items.Count);
        RunOnUi(() =>
        {
            foreach (ApiEventItem item in items)
            {
                ScheduleEvent e = ToEvent(item);
                created.Add(e);
                _store.Data.Events.Add(e);
            }
            SaveAndRefresh();
        });
        await WriteJson(ctx, 200, new { ok = true, count = created.Count, events = created });
    }

    private async Task UpdateEvent(HttpListenerContext ctx, string id)
    {
        id = Uri.UnescapeDataString(id.Trim());
        string body = await ReadBodyAsync(ctx);
        ApiEventItem item;
        try
        {
            item = JsonSerializer.Deserialize<ApiEventItem>(body, JsonOpts) ?? new ApiEventItem();
        }
        catch (JsonException ex)
        {
            await WriteJson(ctx, 400, new { ok = false, error = "JSON 解析失败：" + ex.Message });
            return;
        }
        if (Validate(item) is { } error)
        {
            await WriteJson(ctx, 400, new { ok = false, error, item = item.Title });
            return;
        }

        bool notFound = false, forbidden = false;
        RunOnUi(() =>
        {
            ScheduleEvent? target = _store.Data.Events.FirstOrDefault(e =>
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                notFound = true;
                return;
            }
            if (!string.Equals(target.Source, ApiSource, StringComparison.OrdinalIgnoreCase))
            {
                forbidden = true;
                return;
            }
            target.Title = item.Title.Trim();
            target.Category = string.IsNullOrWhiteSpace(item.Category) ? EventCategories.Default : item.Category.Trim();
            target.Teacher = item.Teacher ?? "";
            target.Location = item.Location ?? "";
            target.Notes = item.Notes ?? "";
            target.ColorKey = item.ColorKey ?? "";
            target.Weekday = item.Weekday;
            target.StartSection = item.StartSection;
            target.EndSection = item.EndSection;
            target.WeeksRaw = string.IsNullOrWhiteSpace(item.WeeksRaw) ? $"1-{_store.Data.TotalWeeks}周" : item.WeeksRaw.Trim();
            target.Date = string.IsNullOrWhiteSpace(item.Date) ? null : item.Date;
            SaveAndRefresh();
        });

        if (notFound)
        {
            await WriteJson(ctx, 404, new { ok = false, error = "未找到该 API 日程" });
            return;
        }
        if (forbidden)
        {
            await WriteJson(ctx, 403, new { ok = false, error = "该日程不是通过 API 创建，禁止修改" });
            return;
        }
        await WriteJson(ctx, 200, new { ok = true, id });
    }

    private async Task DeleteEvent(HttpListenerContext ctx, string id)
    {
        id = Uri.UnescapeDataString(id.Trim());
        bool notFound = false, forbidden = false;
        RunOnUi(() =>
        {
            ScheduleEvent? target = _store.Data.Events.FirstOrDefault(e =>
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                notFound = true;
                return;
            }
            if (!string.Equals(target.Source, ApiSource, StringComparison.OrdinalIgnoreCase))
            {
                forbidden = true;
                return;
            }
            _store.Data.Events.Remove(target);
            SaveAndRefresh();
        });

        if (notFound)
        {
            await WriteJson(ctx, 404, new { ok = false, error = "未找到该 API 日程" });
            return;
        }
        if (forbidden)
        {
            await WriteJson(ctx, 403, new { ok = false, error = "该日程不是通过 API 创建，禁止删除" });
            return;
        }
        await WriteJson(ctx, 200, new { ok = true, id });
    }

    private void SaveAndRefresh()
    {
        _store.Save();
        _refresh();
    }

    private ScheduleEvent ToEvent(ApiEventItem item) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Title = item.Title.Trim(),
        Category = string.IsNullOrWhiteSpace(item.Category) ? EventCategories.Default : item.Category.Trim(),
        Teacher = item.Teacher ?? "",
        Location = item.Location ?? "",
        Notes = item.Notes ?? "",
        ColorKey = item.ColorKey ?? "",
        Source = ApiSource,
        Weekday = item.Weekday,
        StartSection = item.StartSection,
        EndSection = item.EndSection,
        WeeksRaw = string.IsNullOrWhiteSpace(item.WeeksRaw) ? $"1-{_store.Data.TotalWeeks}周" : item.WeeksRaw.Trim(),
        Date = string.IsNullOrWhiteSpace(item.Date) ? null : item.Date,
    };

    private static string? Validate(ApiEventItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Title)) return "title 不能为空";
        if (item.Weekday is < 1 or > 7) return "weekday 需为 1-7（1=周一）";
        if (item.StartSection < 1 || item.EndSection < item.StartSection) return "startSection/endSection 无效";
        if (!string.IsNullOrWhiteSpace(item.WeeksRaw))
        {
            try
            {
                WeekRule.Parse(item.WeeksRaw);
            }
            catch (FormatException)
            {
                return "weeksRaw 周次表达式无效（示例：1-16周 / 1-9周(单) / 1,3,5周）";
            }
        }
        if (!string.IsNullOrWhiteSpace(item.Date) &&
            !DateTime.TryParseExact(item.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
        {
            return "date 需为 yyyy-MM-dd";
        }
        return null;
    }

    private static async Task<string> ReadBodyAsync(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task WriteJson(HttpListenerContext ctx, int status, object? payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    /// <summary>所有数据读写切到 UI 线程执行，与插件宿主保持一致。</summary>
    private void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}

/// <summary>API 请求体字段（与插件事件字段一致，不含 Source/Id——来源固定为 "api"）。</summary>
public sealed class ApiEventItem
{
    public string Title { get; set; } = "";

    public string Category { get; set; } = "";

    public string Teacher { get; set; } = "";

    public string Location { get; set; } = "";

    public string Notes { get; set; } = "";

    public string ColorKey { get; set; } = "";

    /// <summary>1=周一 ... 7=周日。</summary>
    public int Weekday { get; set; } = 1;

    public int StartSection { get; set; } = 1;

    public int EndSection { get; set; } = 1;

    public string WeeksRaw { get; set; } = "";

    /// <summary>单次日程日期（yyyy-MM-dd）；为空表示按周重复。</summary>
    public string? Date { get; set; }
}
