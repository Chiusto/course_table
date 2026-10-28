// 冒烟测试：直接调用 NcuCourseSource 插件的 LoginService（与插件窗口“登录并同步”同一代码路径），
// 在未设置 PYTHONPATH 的环境下运行，验证环境探测与 KeyNotFoundException 修复。
using NcuCourseSource;

string dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "NcuCourseTable");
var vault = new CredentialVault(dataDir);
var cred = vault.Read() ?? throw new InvalidOperationException("vault 中无凭据");
Console.WriteLine($"账号: {Mask(cred.Username)}，密码长度: {cred.Password.Length}");

var svc = new LoginService(dataDir);
Console.WriteLine($"PYTHONPATH 环境变量: {(Environment.GetEnvironmentVariable("PYTHONPATH") is { } p ? p : "<未设置>")}");
Console.WriteLine("python = " + (svc.GetType()
    .GetField("_python", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
    .GetValue(svc) as Lazy<string?>)!.Value ?? "<null>");

SyncOutcome outcome = svc.LoginAndSyncAsync(cred.Username, cred.Password).GetAwaiter().GetResult();
Console.WriteLine($"Ok={outcome.Ok}, CourseCount={outcome.CourseCount}, Term={outcome.Termcode}");
Console.WriteLine($"Message: {outcome.Message}");
if (!string.IsNullOrEmpty(outcome.Detail)) Console.WriteLine($"Detail: {outcome.Detail}");
return outcome.Ok ? 0 : 1;

static string Mask(string s) => s.Length <= 5 ? s : s[..5] + "***";
