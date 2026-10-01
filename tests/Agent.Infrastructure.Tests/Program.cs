// ============================================================
// Agent.Infrastructure.Tests / Program.cs
// 确定性回归（零依赖断言 harness，无需 nuget 测试框架）：
//   1. OutputVerifier 输出判定（凭据 / stderr 剥离 / 结构 / 通用失败+双容错 / 占位 / 模板）
//   2. WriteFileTool 写入时 timeout 静态检查（§14-3h）
// 运行：dotnet run --project tests/Agent.Infrastructure.Tests
// 退出码：0 = 全部通过；1 = 存在失败。
// 历史：原 %TEMP%\lawcheck\ovtest（临时工程），10-01 P0 收敛入库。
// ============================================================
using System.Reflection;
using System.Text.Json;

var asm = Assembly.Load("Agent.Infrastructure");
var t = asm.GetType("Agent.Infrastructure.Quality.OutputVerifier", throwOnError: true)!;

static object? Invoke(MethodInfo m, params object?[] args)
    => m.Invoke(null, args);

static MethodInfo M(Type t, string name, Type[] ptypes)
{
    var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, ptypes, null)
        ?? throw new Exception("missing method " + name);
    return m;
}

// ── 方法句柄 ──
var mCred = M(t, "HasCredentialFailure", new[] { typeof(string), typeof(string).MakeByRefType() });
var mStrip = M(t, "StripHarmlessWarnings", new[] { typeof(string) });
var mStruct = M(t, "HasStructuralError", new[] { typeof(string), typeof(string).MakeByRefType() });
var mGeneric = M(t, "HasGenericFailure", new[] { typeof(string), typeof(string), typeof(string).MakeByRefType() });
var mPhKey = M(t, "HasPlaceholderKey", new[] { typeof(IReadOnlyList<string>), typeof(string).MakeByRefType() });
var mPhOut = M(t, "IsPlaceholderOutput", new[] { typeof(IReadOnlyList<string>), typeof(int).MakeByRefType(), typeof(int).MakeByRefType() });
var mTmpl = M(t, "IsTemplateOutput", new[] { typeof(IReadOnlyList<string>) });

var pass = 0; var fail = 0;

void Check(string name, bool cond)
{
    if (cond) { pass++; Console.WriteLine($"  [OK] {name}"); }
    else { fail++; Console.WriteLine($"  [FAIL] {name}"); }
}

bool Cred(string output)
{
    var args = new object?[] { output, null };
    var r = (bool)Invoke(mCred, args)!;
    return r;
}

string? Strip(string stderr) => (string?)Invoke(mStrip, stderr);

bool Generic(string stdout, string? expected)
{
    var args = new object?[] { stdout, expected, null };
    return (bool)Invoke(mGeneric, args)!;
}

bool Struct(string stdout)
{
    var args = new object?[] { stdout, null };
    return (bool)Invoke(mStruct, args)!;
}

bool PhKey(params string[] lines) => (bool)Invoke(mPhKey, new object?[] { (IReadOnlyList<string>)lines.ToList(), null })!;

Console.WriteLine("== 凭据失败（检查 0.5） ==");
Check("401 JSON", Cred("错误: 401 - {\"cod\":401, \"message\": \"Invalid api key\"}"));
Check("403 Forbidden", Cred("HTTP错误: 403 - Forbidden"));
Check("api_key invalid", Cred("获取天气信息失败: 401 (api_key invalid)"));
Check("unauthorized", Cred("Error: unauthorized access"));
Check("正常输出不误伤", !Cred("IP: 8.8.8.8 / Temperature: 21°C / Humidity: 65%"));

Console.WriteLine("== stderr 剥离（检查 2） ==");
var pureWarn = "C:\\...\\requests\\__init__.py:113: RequestsDependencyWarning: urllib3 (2.7.0) or chardet (7.4.3) doesn't match a supported version!\n  warnings.warn(\n    \"urllib3 ({0}) or chardet ({1}) doesn't match a supported version!\",\n    RequestsDependencyWarning)";
Check("纯 Warning 剥离后为空", string.IsNullOrWhiteSpace(Strip(pureWarn)));
var warnPlusErr = pureWarn + "\nTraceback (most recent call last):\nValueError: boom";
Check("Warning+真错误 剥离后非空", !string.IsNullOrWhiteSpace(Strip(warnPlusErr)));
Check("NETSDK 警告行被过滤", string.IsNullOrWhiteSpace(Strip("warning NETSDK1174: Ignoring imported project")));

Console.WriteLine("== 结构性错误（检查 3） ==");
Check("error CS", Struct("Program.cs(8,9): error CS0272: 属性或索引器不可用"));
Check("Traceback", Struct("Traceback (most recent call last):\n  NameError: name 'x' is not defined"));
Check("ZeroDivisionError（并集新增）", Struct("ZeroDivisionError: division by zero"));
Check("找不到（中文）", Struct("找不到文件 xxx"));
Check("成功输出不误伤", !Struct("IP: 8.8.8.8 / Temperature: 21°C / Humidity: 65%"));

Console.WriteLine("== 通用失败词 + JSON + 双容错（检查 3.5） ==");
Check("JSON error 命中", Generic("{\"error\": \"Connection aborted...\"}", null));
Check("JSON error_type 命中", Generic("{\"error_type\": \"URLError\"}", null));
Check("网络错误: 命中", Generic("网络错误: ...ip-api.com timed out", null));
Check("取款失败 命中", Generic("取款失败：余额不足", null));
Check("未找到 命中（查找类）", Generic("未找到ID为1的产品", null));
Check("expected 含失败词 → 放行", !Generic("取款失败：余额不足", "取款失败：余额不足"));
Check("expected 含 JSON error → 放行", !Generic("{\"error\": \"x\"}", "{\"error\": \"x\"}"));
Check("成功报告 → 放行", !Generic("检查完成：失败 0 条，成功 5 条", null));
Check("wttr.in 成功 → 放行", !Generic("IP: 8.8.8.8 / Temperature: 21°C / Humidity: 65%", null));
Check("expected 含未找到 → 放行", !Generic("未找到ID为1的产品", "未找到ID为1的产品"));

Console.WriteLine("== 占位 Key / 占位符 / 模板（检查 4） ==");
Check("占位 Key 命中", PhKey("请在代码中替换YOUR_OPENWEATHERMAP_API_KEY为您的实际API密钥"));
Check("占位符过半命中", (bool)Invoke(mPhOut, new object?[] { (IReadOnlyList<string>)new List<string> { "?", "N/A", "OK" }, null, null })!);
Check("模板单行 hello 命中", (bool)Invoke(mTmpl, new object?[] { (IReadOnlyList<string>)new List<string> { "hello" } })!);
Check("模板 success 命中", (bool)Invoke(mTmpl, new object?[] { (IReadOnlyList<string>)new List<string> { "success" } })!);
Check("多行正常输出不判模板", !(bool)Invoke(mTmpl, new object?[] { (IReadOnlyList<string>)new List<string> { "IP: 8.8.8.8", "Temperature: 21°C" } })!);

Console.WriteLine("== WriteFileTool 写入时 timeout 静态检查（§14-3h） ==");
{
    var ws = Path.Combine(Path.GetTempPath(), "lawcheck", "tests_timeout");
    if (Directory.Exists(ws)) Directory.Delete(ws, true);
    Directory.CreateDirectory(ws);
    var tool = new Agent.Infrastructure.Tools.WriteFileTool(ws);

    string WriteAndRead(string path, string content)
    {
        var json = $"{{\"path\":\"{path}\",\"content\":{JsonSerializer.Serialize(content)}}}";
        var args = JsonSerializer.Deserialize<JsonElement>(json);
        tool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
        return File.ReadAllText(Path.Combine(ws, path.Replace('/', Path.DirectorySeparatorChar)));
    }

    Check("requests.get 无 timeout → 补 timeout=8",
        WriteAndRead("a.py", "r = requests.get(\"http://ip-api.com/json/\")").Contains(", timeout=8"));
    var b = WriteAndRead("b.py", "r = requests.get(\"http://x\", timeout=8)");
    Check("requests.get 已带 timeout → 不动",
        b.Split("timeout=8").Length == 2);
    Check("跨行调用 → 不动",
        !WriteAndRead("c.py", "r = requests.get(\n    \"http://x\"\n)").Contains("timeout"));
    Check("urllib urlopen → 补 timeout",
        WriteAndRead("d.py", "r = urllib.request.urlopen(url)").Contains("urlopen(url, timeout=8)"));
    Check("非 .py 文件 → 不动",
        !WriteAndRead("note.txt", "r = requests.get(\"http://x\")").Contains("timeout"));
}

Console.WriteLine($"\n结果: {pass} 通过 / {fail} 失败");
return fail == 0 ? 0 : 1;
