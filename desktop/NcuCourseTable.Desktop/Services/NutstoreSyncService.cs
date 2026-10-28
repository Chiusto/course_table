using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using NcuCourseTable.Desktop.Models;

namespace NcuCourseTable.Desktop.Services;

public sealed record ConnectionTestResult(bool Success, string Message);

public sealed record SyncOutcome(
    bool Success,
    string Message,
    bool AppliedLocal,
    bool Uploaded,
    DateTime? LastSyncUtc,
    int Conflicts)
{
    public static SyncOutcome Failure(string message) => new(false, message, false, false, null, 0);
}

/// <summary>
/// 坚果云 WebDAV 双向同步。
/// 首次同步把云端与本地合并；之后以 nutstore-state.json 的 Base 做三方合并：
/// 单侧改动直接传播，双方改同一条目按 UpdatedAt 取较新版本，单侧删除会传播。
/// </summary>
public sealed class NutstoreSyncService : IDisposable
{
    private const string PropFindBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<d:propfind xmlns:d=\"DAV:\"><d:prop>" +
        "<d:getetag/><d:getcontentlength/><d:resourcetype/>" +
        "</d:prop></d:propfind>";

    private readonly DataStore _store;
    private readonly NutstoreSettings _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NutstoreSyncService(DataStore store, NutstoreSettings settings)
    {
        _store = store;
        _settings = settings;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(35),
        };
    }

    public async Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Validate(requireRemotePath: false);
            using HttpResponseMessage response = await PropFindAsync(ServerRootUrl(), ct);
            if (!response.IsSuccessStatusCode) throw HttpError(response);
            return new ConnectionTestResult(true, "连接成功：账号与应用密码有效。");
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, Friendly(ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SyncOutcome> SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Validate(requireRemotePath: true);

            bool localFileMissing = !File.Exists(_store.FilePath);
            if (localFileMissing) _store.Save();
            StoreData local = ReadLocal();

            string remoteKey = BuildRemoteKey();
            NutstoreSyncState state = NutstoreSyncState.Load(_store.Dir);
            if (!string.Equals(state.RemoteKey, remoteKey, StringComparison.OrdinalIgnoreCase))
            {
                state = new NutstoreSyncState { RemoteKey = remoteKey };
            }

            await EnsureRemoteDirectoryAsync(ct);
            string fileUrl = BuildFileUrl();
            RemoteFileInfo? info = await PropFindFileAsync(fileUrl, ct);

            if (info is null)
            {
                string createdEtag = await PutAsync(fileUrl, local, createOnly: true, ifMatch: null, ct);
                return new SyncOutcome(true,
                    "已上传到坚果云（云端此前没有存档）。" + SaveState(state, remoteKey, createdEtag, local),
                    false, true, DateTime.UtcNow, 0);
            }

            if (!localFileMissing && state.Base is not null &&
                !string.IsNullOrEmpty(info.ETag) &&
                string.Equals(state.RemoteETag, info.ETag, StringComparison.Ordinal) &&
                DataEquals(local, state.Base))
            {
                return new SyncOutcome(true,
                    "已是最新，无需同步。" + SaveState(state, remoteKey, info.ETag, state.Base),
                    false, false, DateTime.UtcNow, 0);
            }

            StoreData remote = await GetAsync(fileUrl, ct);

            if (localFileMissing && state.Base is not null)
            {
                EnsureLocalUnchanged(local);
                _store.Replace(remote);
                return new SyncOutcome(true,
                    "本地数据缺失，已从坚果云恢复。" + SaveState(state, remoteKey, info.ETag, remote),
                    true, false, DateTime.UtcNow, 0);
            }

            if (DataEquals(local, remote))
            {
                return new SyncOutcome(true,
                    "云端与本地一致。" + SaveState(state, remoteKey, info.ETag, remote),
                    false, false, DateTime.UtcNow, 0);
            }

            bool localUnchanged = state.Base is not null && DataEquals(local, state.Base);
            bool remoteUnchanged = state.Base is not null && DataEquals(remote, state.Base);

            if (localUnchanged)
            {
                EnsureLocalUnchanged(local);
                _store.Replace(remote);
                return new SyncOutcome(true,
                    "已拉取云端更改。" + SaveState(state, remoteKey, info.ETag, remote),
                    true, false, DateTime.UtcNow, 0);
            }

            if (remoteUnchanged)
            {
                EnsureLocalUnchanged(local);
                string etag = await PutAsync(fileUrl, local, createOnly: false, ifMatch: info.ETag, ct);
                return new SyncOutcome(true,
                    "已上传本地更改。" + SaveState(state, remoteKey, etag, local),
                    false, true, DateTime.UtcNow, 0);
            }

            SyncMerge.MergeResult merge = SyncMerge.Merge(local, remote, state.Base);

            if (DataEquals(merge.Data, remote))
            {
                EnsureLocalUnchanged(local);
                _store.Replace(merge.Data);
                return new SyncOutcome(true,
                    MergeMessage("已合并并应用云端数据", merge.Conflicts) +
                    SaveState(state, remoteKey, info.ETag, merge.Data),
                    true, false, DateTime.UtcNow, merge.Conflicts);
            }

            bool applyLocal = !DataEquals(merge.Data, local);
            EnsureLocalUnchanged(local);
            string mergedEtag = await PutAsync(fileUrl, merge.Data, createOnly: false, ifMatch: info.ETag, ct);
            EnsureLocalUnchanged(local);
            if (applyLocal) _store.Replace(merge.Data);
            return new SyncOutcome(true,
                MergeMessage("已合并云端与本地数据并上传", merge.Conflicts) +
                SaveState(state, remoteKey, mergedEtag, merge.Data),
                applyLocal, true, DateTime.UtcNow, merge.Conflicts);
        }
        catch (Exception ex)
        {
            return SyncOutcome.Failure(Friendly(ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    // ------------------------------------------------------------------ 校验与读取

    private void Validate(bool requireRemotePath)
    {
        _settings.Normalize();
        if (_settings.Username.Length == 0 || _settings.Password.Length == 0)
        {
            throw new NutstoreSyncException("请先在设置中填写坚果云账号与应用密码。");
        }
        if (!Uri.TryCreate(_settings.Server, UriKind.Absolute, out Uri? uri) ||
            uri.Host.Length == 0 ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new NutstoreSyncException("WebDAV 地址必须是有效的 https:// 链接。");
        }
        if (requireRemotePath) _ = RemoteSegments();
    }

    private StoreData ReadLocal()
    {
        try
        {
            StoreData? data = JsonSerializer.Deserialize<StoreData>(
                File.ReadAllText(_store.FilePath), DataStore.JsonOpts);
            return ValidateData(data, "本地 schedule.json");
        }
        catch (JsonException)
        {
            throw new NutstoreSyncException("本地 schedule.json 无法解析，已中止同步以保护数据。");
        }
        catch (IOException ex)
        {
            throw new NutstoreSyncException("读取本地时间表失败：" + ex.Message);
        }
    }

    private void EnsureLocalUnchanged(StoreData snapshot)
    {
        if (!DataEquals(ReadLocal(), snapshot))
        {
            throw new NutstoreSyncException(
                "本地数据在同步过程中被修改，已中止本次同步，请重试。");
        }
    }

    private static StoreData ValidateData(StoreData? data, string label)
    {
        if (data is null || data.Events is null || data.Todos is null ||
            data.Sections is null || data.Links is null)
        {
            throw new NutstoreSyncException($"{label} 不是有效的时间表数据。");
        }
        return data;
    }

    private static bool DataEquals(StoreData a, StoreData b) =>
        string.Equals(JsonSerializer.Serialize(a, DataStore.JsonOpts),
            JsonSerializer.Serialize(b, DataStore.JsonOpts), StringComparison.Ordinal);

    private string SaveState(
        NutstoreSyncState state, string remoteKey, string etag, StoreData baseData)
    {
        state.RemoteKey = remoteKey;
        state.RemoteETag = etag ?? "";
        state.Base = baseData;
        try
        {
            state.Save(_store.Dir);
            return "";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"nutstore-state.json 保存失败：{ex.Message}");
            return "（本地同步基线保存失败，下次同步会重新比对。）";
        }
    }

    private static string MergeMessage(string prefix, int conflicts) =>
        conflicts > 0
            ? $"{prefix}（{conflicts} 处冲突已自动解决）。"
            : prefix + "。";

    // ------------------------------------------------------------------ WebDAV

    private async Task<HttpResponseMessage> PropFindAsync(string url, CancellationToken ct)
    {
        using HttpRequestMessage request = NewRequest(new HttpMethod("PROPFIND"), url);
        request.Headers.TryAddWithoutValidation("Depth", "0");
        request.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<RemoteFileInfo?> PropFindFileAsync(string url, CancellationToken ct)
    {
        using HttpResponseMessage response = await PropFindAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw HttpError(response);

        XDocument document;
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            document = XDocument.Load(stream);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
        {
            throw new NutstoreSyncException("无法解析坚果云返回的 WebDAV 信息。");
        }

        XNamespace dav = "DAV:";
        XElement? prop = document.Descendants(dav + "prop").FirstOrDefault();
        string etag = prop?.Element(dav + "getetag")?.Value.Trim() ?? "";
        bool isDirectory = prop?.Element(dav + "resourcetype")?.Element(dav + "collection") is not null;
        if (isDirectory)
        {
            throw new NutstoreSyncException("远程路径指向文件夹，请填写具体的 .json 文件路径。");
        }
        return new RemoteFileInfo(etag);
    }

    private async Task<StoreData> GetAsync(string url, CancellationToken ct)
    {
        using HttpRequestMessage request = NewRequest(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using HttpResponseMessage response =
            await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw HttpError(response);

        string json = await response.Content.ReadAsStringAsync(ct);
        try
        {
            StoreData? data = JsonSerializer.Deserialize<StoreData>(json, DataStore.JsonOpts);
            return ValidateData(data, "云端 schedule.json");
        }
        catch (JsonException)
        {
            throw new NutstoreSyncException("云端 schedule.json 格式无效，已保持本地数据不变。");
        }
    }

    private async Task<string> PutAsync(
        string url, StoreData data, bool createOnly, string? ifMatch, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(data, DataStore.JsonOpts);
        using HttpRequestMessage request = NewRequest(HttpMethod.Put, url);
        if (createOnly)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }
        else if (!string.IsNullOrEmpty(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        var content = new ByteArrayContent(new UTF8Encoding(false).GetBytes(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Content = content;

        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw HttpError(response);

        string etag = response.Headers.ETag?.ToString() ?? "";
        if (etag.Length == 0)
        {
            RemoteFileInfo? info = await PropFindFileAsync(url, ct);
            etag = info?.ETag ?? "";
        }
        return etag;
    }

    private async Task EnsureRemoteDirectoryAsync(CancellationToken ct)
    {
        string[] segments = RemoteSegments();
        for (int count = 1; count < segments.Length; count++)
        {
            string dirUrl = BuildUrl(segments, count, directory: true);
            using HttpRequestMessage request = NewRequest(new HttpMethod("MKCOL"), dirUrl);
            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) continue;
            if ((int)response.StatusCode == 405) continue; // 已存在
            throw HttpError(response);
        }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        string token = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_settings.Username}:{_settings.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        request.Headers.TryAddWithoutValidation("User-Agent", "Timetable/2.0 (NutstoreSync)");
        return request;
    }

    private string ServerRootUrl()
    {
        string server = _settings.Server;
        return server.EndsWith('/') ? server : server + "/";
    }

    private string BuildFileUrl()
    {
        string[] segments = RemoteSegments();
        return BuildUrl(segments, segments.Length, directory: false);
    }

    private string BuildUrl(string[] segments, int count, bool directory)
    {
        string path = string.Join("/", segments.Take(count).Select(Uri.EscapeDataString));
        return directory ? ServerRootUrl() + path + "/" : ServerRootUrl() + path;
    }

    private string[] RemoteSegments()
    {
        string[] segments = _settings.RemotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new NutstoreSyncException("远程文件路径不能为空。");
        }
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new NutstoreSyncException("远程文件路径不能包含 . 或 .. 路径片段。");
        }
        return segments;
    }

    private string BuildRemoteKey() =>
        $"{_settings.Server.TrimEnd('/')}|{_settings.RemotePath}|{_settings.Username.ToLowerInvariant()}";

    private static NutstoreSyncException HttpError(HttpResponseMessage response) =>
        (int)response.StatusCode switch
        {
            401 => new NutstoreSyncException(
                "认证失败：请填写坚果云账号邮箱与应用密码（不是登录密码）。"),
            403 => new NutstoreSyncException(
                "服务器拒绝访问（403），请检查账号权限或坚果云空间状态。"),
            404 => new NutstoreSyncException(
                "远程路径不存在（404），请检查 WebDAV 地址与远程文件路径。"),
            409 => new NutstoreSyncException(
                "远程路径冲突（409），请确认文件夹可用且不是同名文件。"),
            412 => new NutstoreSyncException(
                "云端文件刚被其他设备修改，请重试同步。"),
            423 => new NutstoreSyncException(
                "云端文件被锁定（423），请稍后重试。"),
            507 => new NutstoreSyncException("坚果云空间不足（507）。"),
            _ => new NutstoreSyncException(
                $"坚果云返回错误：{(int)response.StatusCode} {response.ReasonPhrase}"),
        };

    private static string Friendly(Exception ex) => ex switch
    {
        NutstoreSyncException => ex.Message,
        OperationCanceledException => "请求超时或已取消，请检查网络后重试。",
        HttpRequestException request => "无法连接坚果云：" + request.Message,
        JsonException => "同步数据格式错误，已中止。",
        IOException io => "本地文件读写失败：" + io.Message,
        UnauthorizedAccessException => "没有权限读写本地数据目录。",
        _ => "同步失败：" + ex.Message,
    };

    private sealed record RemoteFileInfo(string ETag);
}

internal sealed class NutstoreSyncException : Exception
{
    public NutstoreSyncException(string message) : base(message)
    {
    }
}
