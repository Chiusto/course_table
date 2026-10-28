using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using NcuCourseTable.Desktop.Services;
using NcuCourseTable.Desktop.ViewModels;

namespace NcuCourseTable.Desktop;

/// <summary>
/// 坚果云同步生命周期：启动时自动拉取、本地保存后防抖上传、手动同步与状态回刷。
/// 所有文件读写与 UI 刷新都发生在 UI 线程；网络请求异步进行。
/// </summary>
public partial class App
{
    private NutstoreSettings? _nutstoreSettings;
    private NutstoreSyncService? _nutstore;
    private DispatcherTimer? _nutstoreDebounce;
    private Task<SyncOutcome>? _nutstoreSyncTask;
    private bool _nutstoreSyncPending;
    private bool _nutstoreBusy;
    private bool _shuttingDown;
    private string _nutstoreBusyText = "正在同步…";

    private void InitializeNutstore()
    {
        if (_store is null) return;
        _nutstoreSettings = NutstoreSettings.Load(_store.Dir);
        _nutstore = new NutstoreSyncService(_store, _nutstoreSettings);
        _store.Saved += OnStoreSaved;
    }

    private void ScheduleStartupNutstoreSync()
    {
        if (_nutstoreSettings is not { AutoSync: true } || !_nutstoreSettings.IsConfigured) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _ = SyncNutstoreNowAsync(announce: false);
        }), DispatcherPriority.ApplicationIdle);
    }

    private void OnStoreSaved()
    {
        if (_shuttingDown ||
            _nutstoreSettings is not { AutoSync: true } ||
            !_nutstoreSettings.IsConfigured)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(OnStoreSaved));
            return;
        }

        _nutstoreDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _nutstoreDebounce.Tick -= OnNutstoreDebounceTick;
        _nutstoreDebounce.Tick += OnNutstoreDebounceTick;
        _nutstoreDebounce.Stop();
        _nutstoreDebounce.Start();
    }

    private void OnNutstoreDebounceTick(object? sender, EventArgs e)
    {
        _nutstoreDebounce?.Stop();
        if (!_shuttingDown && _nutstoreSettings is { AutoSync: true } && _nutstoreSettings.IsConfigured)
        {
            _ = SyncNutstoreNowAsync(announce: false);
        }
    }

    internal Task<SyncOutcome> SyncNutstoreNowAsync(bool announce)
    {
        if (_nutstoreSyncTask is { IsCompleted: false })
        {
            // 同步进行中又有新改动：完成后补一次，避免这次快照漏掉刚保存的数据。
            _nutstoreSyncPending = true;
            return _nutstoreSyncTask;
        }
        _nutstoreSyncTask = RunNutstoreSyncAsync(announce);
        return _nutstoreSyncTask;
    }

    private async Task<SyncOutcome> RunNutstoreSyncAsync(bool announce)
    {
        if (_nutstore is null || _nutstoreSettings is null || _store is null)
        {
            return SyncOutcome.Failure("坚果云同步尚未初始化。");
        }
        if (!_nutstoreSettings.IsConfigured)
        {
            const string message = "请先在设置中填写坚果云账号与应用密码。";
            if (announce)
            {
                _tray?.ShowBalloonTip(3000, "坚果云同步",
                    message, System.Windows.Forms.ToolTipIcon.Warning);
            }
            return SyncOutcome.Failure(message);
        }

        SetNutstoreBusy(true, "正在与坚果云同步…");
        try
        {
            SyncOutcome outcome = await _nutstore.SyncAsync();
            _nutstoreSettings.LastSyncStatus = outcome.Message;
            _nutstoreSettings.LastSyncOk = outcome.Success;
            if (outcome.Success && outcome.LastSyncUtc is { } at)
            {
                _nutstoreSettings.LastSyncUtc = at.ToString("O", CultureInfo.InvariantCulture);
            }
            TrySaveNutstoreSettings();

            if (outcome.AppliedLocal) RefreshAll();
            NotifySyncStatus(outcome.Message);

            if (announce)
            {
                _tray?.ShowBalloonTip(3500,
                    outcome.Success ? "坚果云同步完成" : "坚果云同步失败",
                    outcome.Message,
                    outcome.Success
                        ? System.Windows.Forms.ToolTipIcon.Info
                        : System.Windows.Forms.ToolTipIcon.Warning);
            }
            return outcome;
        }
        catch (Exception ex)
        {
            string message = "同步失败：" + ex.Message;
            _nutstoreSettings.LastSyncStatus = message;
            _nutstoreSettings.LastSyncOk = false;
            TrySaveNutstoreSettings();
            NotifySyncStatus(message);
            return SyncOutcome.Failure(message);
        }
        finally
        {
            SetNutstoreBusy(false);
            if (_nutstoreSyncPending && !_shuttingDown)
            {
                _nutstoreSyncPending = false;
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_shuttingDown && _nutstoreSettings?.IsConfigured == true)
                    {
                        _ = SyncNutstoreNowAsync(announce: false);
                    }
                }), DispatcherPriority.Background);
            }
        }
    }

    internal async Task<ConnectionTestResult> TestNutstoreAsync()
    {
        if (_nutstore is null || _nutstoreSettings is null || _store is null)
        {
            return new ConnectionTestResult(false, "坚果云同步尚未初始化。");
        }
        if (!_nutstoreSettings.IsConfigured)
        {
            return new ConnectionTestResult(false, "请先填写 WebDAV 地址、账号与应用密码。");
        }

        SetNutstoreBusy(true, "正在连接坚果云…");
        try
        {
            ConnectionTestResult result = await _nutstore.TestAsync();
            _nutstoreSettings.LastSyncStatus = result.Message;
            _nutstoreSettings.LastSyncOk = result.Success;
            TrySaveNutstoreSettings();
            NotifySyncStatus(result.Message);
            return result;
        }
        catch (Exception ex)
        {
            string message = "测试连接失败：" + ex.Message;
            NotifySyncStatus(message);
            return new ConnectionTestResult(false, message);
        }
        finally
        {
            SetNutstoreBusy(false);
        }
    }

    internal bool SaveNutstoreSettings(
        string server,
        string username,
        string password,
        string remotePath,
        bool autoSync,
        out string message)
    {
        if (_nutstoreSettings is null || _store is null)
        {
            message = "坚果云同步尚未初始化。";
            return false;
        }

        string effectivePassword = string.IsNullOrEmpty(password)
            ? _nutstoreSettings.Password
            : password;
        var probe = new NutstoreSettings
        {
            Server = server,
            Username = username,
            RemotePath = remotePath,
            Password = effectivePassword,
            AutoSync = autoSync,
        };
        probe.Normalize();

        if (!Uri.TryCreate(probe.Server, UriKind.Absolute, out Uri? uri) ||
            uri.Host.Length == 0 ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            message = "WebDAV 地址必须是有效的 https:// 链接。";
            return false;
        }
        if (probe.RemotePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or ".."))
        {
            message = "远程文件路径不能包含 . 或 .. 路径片段。";
            return false;
        }

        _nutstoreSettings.Server = probe.Server;
        _nutstoreSettings.Username = probe.Username;
        _nutstoreSettings.RemotePath = probe.RemotePath;
        _nutstoreSettings.AutoSync = autoSync;
        _nutstoreSettings.Password = effectivePassword;

        try
        {
            _nutstoreSettings.Save(_store.Dir);
        }
        catch (Exception ex)
        {
            message = "保存坚果云设置失败：" + ex.Message;
            return false;
        }

        message = _nutstoreSettings.IsConfigured
            ? "已保存坚果云同步设置。"
            : "已保存，但尚未填写完整的账号与应用密码。";
        SetNutstoreBusy(_nutstoreBusy);
        return true;
    }

    private void TrySaveNutstoreSettings()
    {
        try
        {
            _nutstoreSettings?.Save(_store!.Dir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"nutstore.json 保存失败：{ex.Message}");
        }
    }

    private void SetNutstoreBusy(bool busy, string? text = null)
    {
        _nutstoreBusy = busy;
        if (busy && text is not null) _nutstoreBusyText = text;
        RefreshTrayChecks();
    }

    private void NotifySyncStatus(string message)
    {
        if (_manager is { IsLoaded: true } && _manager.DataContext is MainViewModel vm)
        {
            vm.SetStatus(message);
        }
        System.Diagnostics.Debug.WriteLine("[nutstore] " + message);
    }

    private void ShutdownNutstore()
    {
        _shuttingDown = true;
        _nutstoreDebounce?.Stop();
        if (_store is not null) _store.Saved -= OnStoreSaved;
        _nutstore?.Dispose();
        _nutstore = null;
    }

    // ------------------------------------------------------------- 设置面板读取的状态

    internal NutstoreSettings NutstoreSettings => _nutstoreSettings ?? new NutstoreSettings();

    internal bool NutstoreBusy => _nutstoreBusy;

    internal bool NutstoreConfigured => _nutstoreSettings?.IsConfigured == true;

    internal bool NutstoreStatusOk => _nutstoreSettings?.LastSyncOk ?? true;

    internal string NutstoreStatusText
    {
        get
        {
            if (_nutstoreBusy) return _nutstoreBusyText;
            if (_nutstoreSettings is null) return "同步服务未初始化。";
            if (!string.IsNullOrWhiteSpace(_nutstoreSettings.LastSyncStatus))
            {
                return _nutstoreSettings.LastSyncStatus;
            }
            return _nutstoreSettings.IsConfigured
                ? "已配置，尚未同步。"
                : "未配置：填写账号与应用密码后保存。";
        }
    }

    internal string NutstoreLastSyncText
    {
        get
        {
            if (_nutstoreSettings is null || string.IsNullOrEmpty(_nutstoreSettings.LastSyncUtc))
            {
                return "";
            }
            return DateTime.TryParse(_nutstoreSettings.LastSyncUtc,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime time)
                ? $"上次成功同步：{time.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "";
        }
    }
}
