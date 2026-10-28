using System.Diagnostics;
using System.IO;
using System.Text;
using Timetable.Plugins;

namespace NcuCourseSource;

public sealed class SyncOutcome
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Termcode { get; init; } = "";
    public List<PluginEvent> Events { get; init; } = new();

    public int CourseCount => Events.Count;

    public static SyncOutcome Success(string termcode, List<PluginEvent> events, string detail) =>
        new() { Ok = true, Termcode = termcode, Events = events, Detail = detail };

    public static SyncOutcome Failure(string message, string detail) =>
        new() { Ok = false, Message = message, Detail = detail };
}

/// <summary>
/// 插件侧登录执行器：经子进程调用本地 ncu_sdk（login → sync → export），
/// 再把导出的 JSON 解析为插件日程。凭据只经环境变量传给子进程。
/// </summary>
public sealed class LoginService
{
    public const string DbFileName = "schedule.db";
    public const string ImportTempName = "ncu.import.tmp.json";

    private const int ProbeMs = 3000;
    private const int StepTimeoutSeconds = 150;

    public string DataDir { get; }

    private readonly Lazy<string?> _python;
    private readonly Lazy<string?> _sdkDir;
    private readonly Lazy<string?> _vendorDir;

    public LoginService(string dataDir)
    {
        DataDir = dataDir;
        _python = new Lazy<string?>(ResolvePython);
        _sdkDir = new Lazy<string?>(ResolveSdkDir);
        _vendorDir = new Lazy<string?>(ResolveVendorDir);
    }

    public string DbPath => Path.Combine(DataDir, DbFileName);

    public async Task<SyncOutcome> LoginAndSyncAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return SyncOutcome.Failure("账号或密码为空", "请先在“账号配置…”中填写账号与密码。");
        }

        string? py = _python.Value;
        if (py is null)
        {
            return SyncOutcome.Failure("未找到可用的 Python 环境",
                "登录依赖本机 Python 运行 ncu_sdk（需 requests 依赖）。\n" +
                "· 仓库自带依赖在 desktop\\vendor，确认该目录存在；\n" +
                "· 或为本机 Python 安装依赖：pip install -r requirements.txt；\n" +
                "· 或设置环境变量 NCU_PYTHON 指向已装好依赖的 python.exe。");
        }
        string? sdkDir = _sdkDir.Value;
        if (sdkDir is null)
        {
            return SyncOutcome.Failure("未找到 ncu_sdk（南昌大学课程表 SDK）",
                "请设置环境变量 NCU_SDK_DIR 指向 SDK 仓库根目录（含 sdk\\ncu_sdk 的目录），\n" +
                "或将本程序放到仓库 desktop 目录下运行以便自动探测。");
        }

        string term = TermOfToday();
        try
        {
            Directory.CreateDirectory(DataDir);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(StepTimeoutSeconds));

            CliRun login = await RunAsync(new[] { "--db", DbPath, "login" }, username, password, cts.Token);
            if (login.ExitCode != 0) return Classify(login, "登录");

            CliRun sync = await RunAsync(new[] { "--db", DbPath, "sync", "--term", term }, username, password, cts.Token);
            if (sync.ExitCode != 0) return Classify(sync, "同步课表");

            string tmp = Path.Combine(DataDir, ImportTempName);
            try
            {
                CliRun export = await RunAsync(
                    new[] { "--db", DbPath, "export", "--term", term, "--format", "json", "--out", tmp },
                    username, password, cts.Token);
                if (export.ExitCode != 0) return Classify(export, "导出课表");
                if (!File.Exists(tmp))
                {
                    return SyncOutcome.Failure("导出课表失败", "ncu_sdk export 未生成课表文件，请检查 SDK 版本。");
                }
                CourseJsonImporter.ImportResult imported = CourseJsonImporter.ReadFile(tmp, term);
                if (imported.Events.Count == 0)
                {
                    return SyncOutcome.Failure("没有从教务系统取得课程",
                        $"学期 {term} 可能尚未开放课表，或当前账号本学期无课程。\n" +
                        "可稍后重试，或改用“导入 JSON…”。");
                }
                string finalTerm = imported.Termcode.Length > 0 ? imported.Termcode : term;
                return SyncOutcome.Success(finalTerm, imported.Events,
                    $"学期 {finalTerm} 已同步 {imported.Events.Count} 门课。");
            }
            finally
            {
                TryDelete(tmp);
            }
        }
        catch (OperationCanceledException)
        {
            return SyncOutcome.Failure("登录/同步超时",
                "校园网或服务器响应过慢，请稍后重试；校外环境请先连接校园 VPN 后再登录。");
        }
        catch (Exception ex)
        {
            return SyncOutcome.Failure("调用 ncu_sdk 出错", ex.Message);
        }
    }

    private static SyncOutcome Classify(CliRun run, string stage)
    {
        string text = run.Text;
        if (text.Contains("No module named 'ncu_sdk'") || text.Contains("No module named \u0022ncu_sdk\u0022"))
        {
            return SyncOutcome.Failure($"{stage}失败：未找到 ncu_sdk 包",
                "SDK 目录解析异常（PYTHONPATH 未包含 <SDK 根>\\sdk）。\n" +
                "请设置环境变量 NCU_SDK_DIR 指向仓库根目录后重试。\n\n（SDK 输出）\n" + Tail(text));
        }
        if (text.Contains("ModuleNotFoundError") || text.Contains("No module named"))
        {
            return SyncOutcome.Failure($"{stage}失败：Python 缺少依赖包",
                "请先为本机 Python 安装 SDK 依赖：\n" +
                "    pip install -r requirements.txt\n" +
                "（至少需要 requests。）");
        }
        if (text.Contains("LoginError") || text.Contains("验证码") || text.Contains("账号或密码"))
        {
            return SyncOutcome.Failure($"{stage}失败：账号或密码不正确",
                "请重新在“账号配置…”中核对账号（学号/工号）与密码后重试。\n\n（SDK 输出）\n" + Tail(text));
        }
        if (text.Contains("ConnectionError") || text.Contains("Max retries exceeded") ||
            text.Contains("Timeout") || text.Contains("timed out") || text.Contains("SSLError") ||
            text.Contains("无法连接") || text.Contains("Name or service not known"))
        {
            return SyncOutcome.Failure($"{stage}失败：无法连接南昌大学服务器",
                "请确认已连接校园网（校外请先连校园 VPN / 智网）后重试。\n\n（SDK 输出）\n" + Tail(text));
        }
        if (text.Contains("EndpointOutdatedError") || text.Contains("不是 JSON"))
        {
            return SyncOutcome.Failure($"{stage}失败：学校接口似乎已升级",
                "请更新 ncu_sdk（gmsstu 混淆路径等端点可能已变化）。\n\n（SDK 输出）\n" + Tail(text));
        }
        return SyncOutcome.Failure($"{stage}失败：{FirstLine(text, run.ExitCode)}", Tail(text));
    }

    private static string FirstLine(string text, int exitCode)
    {
        foreach (string line in text.Split('\n'))
        {
            string t = line.Trim().TrimStart('[').Trim();
            if (t.Length > 0) return t.Length > 120 ? t[..120] + "…" : t;
        }
        return $"进程退出码 {exitCode}";
    }

    private static string Tail(string text)
    {
        text = text.Trim();
        return text.Length <= 500 ? text : text[^500..];
    }

    private readonly record struct CliRun(int ExitCode, string Text);

    private async Task<CliRun> RunAsync(string[] args, string username, string password, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _python.Value!,
            WorkingDirectory = _sdkDir.Value!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("ncu_sdk.cli");
        foreach (string a in args) psi.ArgumentList.Add(a);

        psi.Environment["NCU_USERNAME"] = username;
        psi.Environment["NCU_PASSWORD"] = password;
        psi.Environment["PYTHONPATH"] = MergePythonPath(Environment.GetEnvironmentVariable("PYTHONPATH"));
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderr = proc.StandardError.ReadToEndAsync();
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw;
        }
        string text = ((await stdout).TrimEnd() + "\n" + (await stderr).TrimEnd()).Trim();
        return new CliRun(proc.ExitCode, text);
    }

    private string MergePythonPath(string? existing)
    {
        var parts = new List<string>();
        if (_vendorDir.Value is { } vendor) parts.Add(vendor);
        string sdkPackageDir = Path.Combine(_sdkDir.Value!, "sdk");
        if (Directory.Exists(sdkPackageDir)) parts.Add(sdkPackageDir);
        parts.Add(_sdkDir.Value!);
        if (!string.IsNullOrWhiteSpace(existing)) parts.Add(existing);
        return string.Join(";", parts);
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch
        {
            // 临时文件清理失败不影响结果
        }
    }

    private string? ResolvePython()
    {
        string? vendor = _vendorDir.Value;
        string? env = Environment.GetEnvironmentVariable("NCU_PYTHON");
        if (!string.IsNullOrWhiteSpace(env) && ProbePython(env, vendor)) return env;
        if (ProbePython("python", vendor)) return "python";
        return ProbePython("py", vendor, "-3") ? "py" : null;
    }

    private static bool ProbePython(string exe, string? vendorDir, params string[] pre)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true };
            foreach (string p in pre) psi.ArgumentList.Add(p);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(vendorDir is null ? "import sys" : "import requests");
            if (vendorDir is not null)
            {
                psi.Environment["PYTHONPATH"] = vendorDir;
            }
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            if (!proc.WaitForExit(ProbeMs))
            {
                try { proc.Kill(); } catch { /* 忽略 */ }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveSdkDir()
    {
        string? env = Environment.GetEnvironmentVariable("NCU_SDK_DIR");
        if (!string.IsNullOrWhiteSpace(env) && LooksLikeSdkRoot(env)) return Path.GetFullPath(env);
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            string? hit = ProbeUpward(start);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static string? ResolveVendorDir()
    {
        string? env = Environment.GetEnvironmentVariable("NCU_VENDOR_DIR");
        if (!string.IsNullOrWhiteSpace(env) && LooksLikeVendor(env)) return Path.GetFullPath(env);

        string? sdkRoot = Environment.GetEnvironmentVariable("NCU_SDK_DIR");
        if (!string.IsNullOrWhiteSpace(sdkRoot) && LooksLikeSdkRoot(sdkRoot))
        {
            string candidate = Path.Combine(Path.GetFullPath(sdkRoot), "desktop", "vendor");
            if (LooksLikeVendor(candidate)) return candidate;
        }
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            string? root = ProbeUpward(start);
            if (root is null) continue;
            string candidate = Path.Combine(root, "desktop", "vendor");
            if (LooksLikeVendor(candidate)) return candidate;
        }
        return null;
    }

    private static bool LooksLikeVendor(string dir)
    {
        try
        {
            return File.Exists(Path.Combine(dir, "requests", "__init__.py"));
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeSdkRoot(string dir)
    {
        try
        {
            return File.Exists(Path.Combine(dir, "sdk", "ncu_sdk", "__init__.py"));
        }
        catch
        {
            return false;
        }
    }

    private static string? ProbeUpward(string start)
    {
        DirectoryInfo? dir = new DirectoryInfo(start);
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (LooksLikeSdkRoot(dir.FullName)) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string TermOfToday(DateTime? today = null)
    {
        DateTime d = (today ?? DateTime.Today).Date;
        return d.Month switch
        {
            >= 9 => Code(d.Year, d.Year + 1, 1),
            1 => Code(d.Year - 1, d.Year, 1),
            _ => Code(d.Year - 1, d.Year, 2),
        };
    }

    private static string Code(int y1, int y2, int term) => $"{y1}{y2}{term}";
}
