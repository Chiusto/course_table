using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NcuCourseTable.Desktop.Models;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// schedule.json 的三方合并：base 为上次成功同步时的内容。
/// 条目按 Id 对齐，双方都改动时按 UpdatedAt 取较新者；缺少时间戳时保留本机，
/// 单侧删除会传播，删除与修改同时发生则优先保留修改后的内容。
/// </summary>
public static class SyncMerge
{
    public sealed record MergeResult(StoreData Data, int Conflicts);

    public static MergeResult Merge(StoreData local, StoreData remote, StoreData? baseData)
    {
        StoreData l = Clone(local);
        StoreData r = Clone(remote);
        StoreData? b = baseData is null ? null : Clone(baseData);
        bool hasBase = b is not null;
        bool localLooksFresh = LooksFresh(l);
        bool remoteLooksFresh = LooksFresh(r);
        bool preferRemoteScalars = !hasBase && localLooksFresh && !remoteLooksFresh;
        bool preferLocalScalars = !hasBase && remoteLooksFresh && !localLooksFresh;
        int conflicts = 0;

        var result = new StoreData
        {
            Version = Math.Max(l.Version, r.Version),
            Termcode = PickString(l.Termcode, r.Termcode, b?.Termcode ?? "", hasBase,
                preferRemoteScalars, ref conflicts),
            TermName = PickString(l.TermName, r.TermName, b?.TermName ?? "", hasBase,
                preferRemoteScalars, ref conflicts),
            TermStart = PickString(l.TermStart, r.TermStart, b?.TermStart ?? "", hasBase,
                preferRemoteScalars, ref conflicts),
            TotalWeeks = PickWeeks(l.TotalWeeks, r.TotalWeeks, b?.TotalWeeks ?? 0, hasBase,
                preferRemoteScalars, ref conflicts),
            Sections = PickSections(l.Sections, r.Sections, b?.Sections, hasBase,
                preferRemoteScalars, preferLocalScalars, ref conflicts),
            Events = MergeList(l.Events, r.Events, b?.Events, hasBase,
                e => e.Id, e => e.UpdatedAt, ref conflicts),
            Todos = MergeList(l.Todos, r.Todos, b?.Todos, hasBase,
                t => t.Id, t => t.UpdatedAt, ref conflicts),
            Notes = MergeList(l.Notes, r.Notes, b?.Notes, hasBase,
                n => n.Id, n => n.UpdatedAt, ref conflicts),
            Links = MergeMap(l.Links, r.Links, b?.Links, hasBase, ref conflicts),
            LegacyCourses = null,
        };
        result.Events = DedupePluginEvents(result.Events);
        return new MergeResult(result, conflicts);
    }

    /// <summary>
    /// 课表插件每次导入会生成新的随机 Id；首次跨设备同步时，
    /// 同一来源、内容完全相同的课程只保留一条，避免整份课表重复。
    /// 手动日程（local）不参与去重。
    /// </summary>
    private static List<ScheduleEvent> DedupePluginEvents(List<ScheduleEvent> events)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ScheduleEvent>(events.Count);
        foreach (ScheduleEvent e in events)
        {
            if (string.IsNullOrWhiteSpace(e.Source) ||
                string.Equals(e.Source, "local", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(e);
                continue;
            }

            string key = e.Source.ToLowerInvariant() + "|" + EventContentKey(e);
            if (seen.Add(key)) result.Add(e);
        }
        return result;
    }

    private static string EventContentKey(ScheduleEvent e)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(e, DataStore.JsonOpts);
        if (node is JsonObject obj)
        {
            obj.Remove("Id");
            obj.Remove("UpdatedAt");
        }
        return node?.ToJsonString() ?? "";
    }

    /// <summary>没有基线且本机尚无课程 / 学期信息时视为新设备，避免默认设置覆盖云端真实学期。</summary>
    private static bool LooksFresh(StoreData data) =>
        data.Events.Count == 0 &&
        string.IsNullOrWhiteSpace(data.TermName) &&
        string.IsNullOrWhiteSpace(data.Termcode);

    private static string PickString(
        string local,
        string remote,
        string baseValue,
        bool hasBase,
        bool preferRemote,
        ref int conflicts)
    {
        if (!hasBase)
        {
            if (preferRemote) return remote;
            return local.Length == 0 && remote.Length > 0 ? remote : local;
        }
        return PickScalar(local, remote, baseValue, true, ref conflicts);
    }

    private static int PickWeeks(
        int local,
        int remote,
        int baseValue,
        bool hasBase,
        bool preferRemote,
        ref int conflicts)
    {
        if (!hasBase) return preferRemote ? remote : local;
        return PickScalar(local, remote, baseValue, true, ref conflicts);
    }

    private static List<SectionTime> PickSections(
        List<SectionTime> local,
        List<SectionTime> remote,
        List<SectionTime>? baseList,
        bool hasBase,
        bool preferRemote,
        bool preferLocal,
        ref int conflicts)
    {
        if (!hasBase && preferRemote && remote.Count > 0) return remote;
        if (!hasBase && preferLocal && local.Count > 0) return local;
        return MergeList(local, remote, baseList, hasBase,
            s => s.Index.ToString(CultureInfo.InvariantCulture), null, ref conflicts);
    }

    private static T PickScalar<T>(T local, T remote, T baseValue, bool hasBase, ref int conflicts)
    {
        if (!hasBase) return local;
        bool localChanged = !EqualityComparer<T>.Default.Equals(local, baseValue);
        bool remoteChanged = !EqualityComparer<T>.Default.Equals(remote, baseValue);
        if (!localChanged) return remote;
        if (!remoteChanged) return local;
        if (EqualityComparer<T>.Default.Equals(local, remote)) return local;
        conflicts++;
        return local;
    }

    private static List<T> MergeList<T>(
        List<T> local,
        List<T> remote,
        List<T>? baseList,
        bool hasBase,
        Func<T, string> key,
        Func<T, string?>? updatedAt,
        ref int conflicts) where T : class
    {
        Dictionary<string, T> localMap = Index(local, key);
        Dictionary<string, T> remoteMap = Index(remote, key);
        Dictionary<string, T>? baseMap = baseList is null ? null : Index(baseList, key);

        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (T item in local) AddKey(item, key, keys, seen);
        foreach (T item in remote) AddKey(item, key, keys, seen);
        if (baseList is not null)
        {
            foreach (T item in baseList) AddKey(item, key, keys, seen);
        }

        var merged = new List<T>(keys.Count);
        foreach (string k in keys)
        {
            localMap.TryGetValue(k, out T? localItem);
            remoteMap.TryGetValue(k, out T? remoteItem);
            T? baseItem = null;
            if (baseMap is not null) baseMap.TryGetValue(k, out baseItem);

            T? chosen = Choose(localItem, remoteItem, baseItem, hasBase, updatedAt, ref conflicts);
            if (chosen is not null) merged.Add(chosen);
        }
        return merged;
    }

    private static void AddKey<T>(T item, Func<T, string> key, List<string> keys, HashSet<string> seen)
    {
        string k = key(item);
        if (seen.Add(k)) keys.Add(k);
    }

    private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (T item in items) map.TryAdd(key(item), item);
        return map;
    }

    private static T? Choose<T>(
        T? local,
        T? remote,
        T? baseItem,
        bool hasBase,
        Func<T, string?>? updatedAt,
        ref int conflicts) where T : class
    {
        if (local is null && remote is null) return null;

        if (!hasBase)
        {
            if (local is null) return remote;
            if (remote is null) return local;
            if (Same(local, remote)) return local;
            conflicts++;
            return Newer(local, remote, null, updatedAt);
        }

        bool localChanged = !Same(local, baseItem);
        bool remoteChanged = !Same(remote, baseItem);
        if (!localChanged && !remoteChanged) return local;
        if (!localChanged) return remote;
        if (!remoteChanged) return local;
        if (Same(local, remote)) return local;
        conflicts++;
        return Newer(local, remote, baseItem, updatedAt);
    }

    private static T? Newer<T>(T? local, T? remote, T? baseItem, Func<T, string?>? updatedAt)
        where T : class
    {
        if (local is null)
        {
            return KeepPresentOverDeletion(remote, baseItem, updatedAt) ? remote : null;
        }
        if (remote is null)
        {
            return KeepPresentOverDeletion(local, baseItem, updatedAt) ? local : null;
        }

        DateTime localTime = ParseTime(updatedAt?.Invoke(local));
        DateTime remoteTime = ParseTime(updatedAt?.Invoke(remote));
        if (localTime == default && remoteTime == default) return local;
        if (localTime == default) return remote;
        if (remoteTime == default) return local;
        return remoteTime > localTime ? remote : local;
    }

    private static bool KeepPresentOverDeletion<T>(T? present, T? baseItem, Func<T, string?>? updatedAt)
        where T : class
    {
        if (present is null) return false;
        if (baseItem is null || updatedAt is null) return true;
        DateTime itemTime = ParseTime(updatedAt(present));
        DateTime baseTime = ParseTime(updatedAt(baseItem));
        if (itemTime == default || baseTime == default) return true;
        return itemTime > baseTime;
    }

    private static bool Same<T>(T? a, T? b) where T : class
    {
        if (a is null || b is null) return a is null && b is null;
        return string.Equals(DataStore.Fingerprint(a), DataStore.Fingerprint(b), StringComparison.Ordinal);
    }

    private static DateTime ParseTime(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime time)
            ? time
            : default;

    private static Dictionary<string, string> MergeMap(
        Dictionary<string, string> local,
        Dictionary<string, string> remote,
        Dictionary<string, string>? baseMap,
        bool hasBaseData,
        ref int conflicts)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string k in local.Keys) if (seen.Add(k)) keys.Add(k);
        foreach (string k in remote.Keys) if (seen.Add(k)) keys.Add(k);
        if (baseMap is not null)
        {
            foreach (string k in baseMap.Keys) if (seen.Add(k)) keys.Add(k);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string k in keys)
        {
            bool hasLocal = local.TryGetValue(k, out string? localValue);
            bool hasRemote = remote.TryGetValue(k, out string? remoteValue);
            string? baseValue = null;
            bool hasBaseItem = baseMap is not null && baseMap.TryGetValue(k, out baseValue);

            string? chosen;
            if (!hasBaseData)
            {
                if (hasLocal && hasRemote &&
                    !string.Equals(localValue, remoteValue, StringComparison.Ordinal))
                {
                    conflicts++;
                    chosen = localValue;
                }
                else
                {
                    chosen = hasLocal ? localValue : remoteValue;
                }
            }
            else
            {
                bool localChanged = !SameString(hasLocal, localValue, hasBaseItem, baseValue);
                bool remoteChanged = !SameString(hasRemote, remoteValue, hasBaseItem, baseValue);
                if (!localChanged) chosen = hasRemote ? remoteValue : null;
                else if (!remoteChanged) chosen = hasLocal ? localValue : null;
                else if (SameString(hasLocal, localValue, hasRemote, remoteValue)) chosen = hasLocal ? localValue : null;
                else
                {
                    conflicts++;
                    chosen = hasLocal ? localValue : remoteValue;
                }
            }

            if (chosen is not null) result[k] = chosen;
        }
        return result;
    }

    private static bool SameString(bool hasA, string? a, bool hasB, string? b) =>
        hasA == hasB && (!hasA || string.Equals(a, b, StringComparison.Ordinal));

    private static StoreData Clone(StoreData data)
    {
        string json = JsonSerializer.Serialize(data, DataStore.JsonOpts);
        return JsonSerializer.Deserialize<StoreData>(json, DataStore.JsonOpts) ?? new StoreData();
    }
}
