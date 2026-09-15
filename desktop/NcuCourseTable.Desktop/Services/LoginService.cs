using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// 一次登录/同步的执行结果。Ok=false 时 Message 是面向用户的一句话结论，
/// Detail 附解决指引与 SDK 输出尾部；Ok=true 时 CourseCount 为已导入课程数。
/// </summary>
public sealed class LoginOutcome
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public string Detail { get; init; } = "";
    public int CourseCount { get; init; }

    public static LoginOutcome Success(int courseCount, string detail) =>
        new() { Ok = true, CourseCount = courseCount, Detail = detail };

    public static LoginOutcome Failure(string message, string detail) =>
        new() { Ok = false, Message = message, Detail = detail };
}

/// <summary>
/// 桌面侧登录执行器：经子进程调用本地 Python SDK（ncu_sdk.cli）完成真实认证与课表同步——
///   login（CAS 验证账号密码，会话落库 schedule.db）
///   → sync（拉取 gmsstu 真课表，学期按当天自动推算）
///   → export json（写入临时文件）
///   → JsonStore 整学期原子导入 courses.json（桌面卡片的 FileSystemWatcher 随即自动刷新）。
/// 安全：凭据只经环境变量（NCU_USERNAME / NCU_PASSWORD）传给子进程，绝不进入命令行参数。
/// 环境：python / sdk 路径优先取 NCU_PYTHON / NCU_SDK_DIR 环境变量，否则从可执行目录向上
/// 探测仓库布局；SDK 的 Python 依赖（requests 等）优先取仓库自带 desktop\vendor（随仓库
/// 分发、无需用户 pip install），找不到时返回明确失败信息（不静默），与 JsonStore.ResolveDir
/// 的分层解析一致。
/// </summary>
public sealed class LoginService
{
    public const string DbFileName = "schedule.db";
    public const string ImportTempName = "courses.import.tmp.json";

    private const int ProbeMs = 3000;          // python 可用性探测超时
    private const int StepTimeoutSeconds = 150; // 单步（登录/同步/导出）最长时间

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

    /// <summary>登录并同步当前学期课表。耗时操作（网络），调用方在 UI 线程 await 即可。</summary>
    public async Task<LoginOutcome> LoginAndSyncAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return LoginOutcome.Failure("账号或密码为空", "请先在托盘“打开配置…”中填写账号与密码。");
        }

        string? py = _python.Value;
        if (py is null)
        {
            return LoginOutcome.Failure("未找到可用的 Python 环境",
                "登录依赖本机 Python 运行 ncu_sdk（需 requests 依赖）。\n" +
                "· 仓库自带依赖在 desktop\\vendor，确认该目录存在（或重新 checkout 仓库）；\n" +
                "· 或为本机 Python 安装依赖：pip install -r requirements.txt；\n" +
                "· 或设置环境变量 NCU_PYTHON 指向已装好依赖的 python.exe。");
        }
        string? sdkDir = _sdkDir.Value;
        if (sdkDir is null)
        {
            return LoginOutcome.Failure("未找到 ncu_sdk（南昌大学课程表 SDK）",
                "请设置环境变量 NCU_SDK_DIR 指向 SDK 仓库根目录（含 sdk\\ncu_sdk 的目录），\n" +
                "或将本程序放到仓库 desktop 目录下运行以便自动探测。");
        }

        string term = TermOfToday();
        try
        {
            Directory.CreateDirectory(DataDir);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(StepTimeoutSeconds));

            // 1) CAS 登录：校验账号密码，会话写入 schedule.db
            CliRun login = await RunAsync(new[] { "--db", DbPath, "login" }, username, password, cts.Token);
            if (login.ExitCode != 0) return Classify(login, "登录");

            // 2) 同步当前学期真课表
            CliRun sync = await RunAsync(new[] { "--db", DbPath, "sync", "--term", term }, username, password, cts.Token);
            if (sync.ExitCode != 0) return Classify(sync, "同步课表");

            // 3) 导出 json 到临时文件后交给 JsonStore 原子导入（避免直接覆盖损坏现有数据）
            string tmp = Path.Combine(DataDir, ImportTempName);
            try
            {
                CliRun export = await RunAsync(
                    new[] { "--db", DbPath, "export", "--term", term, "--format", "json", "--out", tmp },
                    username, password, cts.Token);
                if (export.ExitCode != 0) return Classify(export, "导出课表");
                if (!File.Exists(tmp))
                {
                    return LoginOutcome.Failure("导出课表失败", "ncu_sdk export 未生成课表文件，请检查 SDK 版本。");
                }
                int n = new JsonStore(DataDir).ImportNcuJson(tmp); // 整学期原子替换 → watcher 自动重载卡片
                return LoginOutcome.Success(n, $"学期 {term} 已同步 {n} 门课，桌面课表已刷新。");
            }
            finally
            {
                TryDelete(tmp);
            }
        }
        catch (OperationCanceledException)
        {
            return LoginOutcome.Failure("登录/同步超时",
                "校园网或服务器响应过慢，请稍后重试；校外环境请先连接校园 VPN 后再登录。");
        }
        catch (Exception ex)
        {
            return LoginOutcome.Failure("调用 ncu_sdk 出错", ex.Message);
        }
    }

    /// <summary>把子进程失败输出翻译成可读结论（按错误类型归类，便于用户自助处理）。</summary>
    private static LoginOutcome Classify(CliRun run, string stage)
    {
        string text = run.Text;
        if (text.Contains("No module named 'ncu_sdk'") || text.Contains("No module named \u0022ncu_sdk\u0022"))
        {
            // ncu_sdk 包本身不可导入：是 PYTHONPATH/SDK 目录配置问题，不是缺三方依赖
            return LoginOutcome.Failure($"{stage}失败：未找到 ncu_sdk 包",
                "SDK 目录解析异常（PYTHONPATH 未包含 <SDK 根>\\sdk）。\n" +
                "请设置环境变量 NCU_SDK_DIR 指向仓库根目录后重试。\n\n（SDK 输出）\n" + Tail(text));
        }
        if (text.Contains("ModuleNotFoundError") || text.Contains("No module named"))
        {
            return LoginOutcome.Failure($"{stage}失败：Python 缺少依赖包",
                "请先为本机 Python 安装 SDK 依赖：\n" +
                "    pip install -r requirements.txt\n" +
                "（至少需要 requests。）");
        }
        if (text.Contains("LoginError") || text.Contains("验证码") || text.Contains("账号或密码"))
        {
            return LoginOutcome.Failure($"{stage}失败：账号或密码不正确",
                "请重新在“打开配置…”中核对账号（学号/工号）与密码后重试。\n\n（SDK 输出）\n" + Tail(text));
        }
        if (text.Contains("ConnectionError") || text.Contains("Max retries exceeded") ||
            text.Contains("Timeout") || text.Contains("timed out") || text.Contains("SSLError") ||
            text.Contains("无法连接") || text.Contains("Name or service not known"))
        {
            return LoginOutcome.Failure($"{stage}失败：无法连接南昌大学服务器",
                "请确认已连接校园网（校外请先连校园 VPN / 智网）后重试。\n\n（SDK 输出）\n" + Tail(text));
        }
        if (text.Contains("EndpointOutdatedError") || text.Contains("不是 JSON"))
        {
            return LoginOutcome.Failure($"{stage}失败：学校接口似乎已升级",
                "请更新 ncu_sdk（gmsstu 混淆路径等端点可能已变化）。\n\n（SDK 输出）\n" + Tail(text));
        }
        return LoginOutcome.Failure($"{stage}失败：{FirstLine(text, run.ExitCode)}", Tail(text));
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

    // ---------------------------------------------------------------- 子进程执行

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

        // 凭据只走环境变量，避免出现在进程命令行（可被其它进程/任务管理器看到）
        psi.Environment["NCU_USERNAME"] = username;
        psi.Environment["NCU_PASSWORD"] = password;
        // 注意：StringDictionary 的 getter 对不存在的键会抛 KeyNotFoundException
        // （.NET Core 实现底层是 Dictionary），必须用环境 API 的空安全读取，
        // 否则用户未设置 PYTHONPATH 时登录直接报“调用 ncu_sdk 出错”。
        psi.Environment["PYTHONPATH"] = MergePythonPath(Environment.GetEnvironmentVariable("PYTHONPATH"));
        psi.Environment["PYTHONIOENCODING"] = "utf-8"; // 保证中文输出按 UTF-8 回流

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
            try { proc.Kill(entireProcessTree: true); } catch { /* 进程已自行退出 */ }
            throw;
        }
        string text = ((await stdout).TrimEnd() + "\n" + (await stderr).TrimEnd()).Trim();
        return new CliRun(proc.ExitCode, text);
    }

    private string MergePythonPath(string? existing)
    {
        // 顺序：仓库自带 vendor（requests 等三方依赖）→ SDK 包目录 → 已有 PYTHONPATH。
        // 注意 ResolveSdkDir 返回的是“仓库根”（其下为 sdk/ncu_sdk/），而
        // `python -m ncu_sdk.cli` 必须能 import 到 ncu_sdk 包，
        // 因此要把 <sdkRoot>\sdk（包的父目录）加入 PYTHONPATH，缺它必定 ModuleNotFoundError。
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

    // ---------------------------------------------------------------- 环境探测

    private string? ResolvePython()
    {
        // 探测时把 vendor 并入 PYTHONPATH：候选解释器必须能让 `import requests` 通过，
        // 否则选中的 python 在登录第一步就会 ModuleNotFoundError（宁可报“未找到”）。
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
            // vendor 存在 → 连依赖一起自检（import requests）；否则仅验证解释器可用
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

    /// <summary>
    /// 仓库自带依赖目录（desktop\vendor，pip install --target 产物）：
    /// 让登录子进程免于用户手动 pip install。优先环境变量 NCU_VENDOR_DIR，
    /// 否则基于 SDK 根目录推断 &lt;sdkRoot&gt;\desktop\vendor，须含 requests 包才有效。
    /// </summary>
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

    /// <summary>vendor 目录判定：须直接包含 requests 包目录。</summary>
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

    /// <summary>仓库根目录判定：目录下应存在 sdk/ncu_sdk/__init__.py。</summary>
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

    /// <summary>
    /// 与 ncu_sdk.config.termcode_of 相同的“今天所属学期”规则（9月~次年1月为第1学期，
    /// 2~8月为第2学期），保证 login/sync/export 三步使用同一学期。
    /// </summary>
    public static string TermOfToday(DateTime? today = null)
    {
        DateTime d = (today ?? DateTime.Today).Date;
        return d.Month switch
        {
            >= 9 => Code(d.Year, d.Year + 1, 1),   // 2026-09 → 202620271
            1 => Code(d.Year - 1, d.Year, 1),      // 2027-01 → 202620271
            _ => Code(d.Year - 1, d.Year, 2),      // 2027-02 → 202620272
        };
    }

    private static string Code(int y1, int y2, int term) => $"{y1}{y2}{term}";
}
