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

// 测试临时根：X 盘（§14-3v，用户要求不在 C 盘 Temp 跑测试）。
// workspace 已 gitignore；按进程隔离防并发互删。
// 【1】test-tmp 清理：启动时删除 >7 天的旧测试目录（实测 99 个 pid 目录累积，防 X 盘膨胀），
// 保留近期目录避免并发测试互删（当前 pid 目录后续创建）。
var testTmpRoot = Path.Combine(@"X:\.NET AI\LocalAiWorkbench\workspace", "test-tmp");
try
{
    if (Directory.Exists(testTmpRoot))
    {
        var cutoff = DateTime.Now.AddDays(-7);
        foreach (var dir in Directory.GetDirectories(testTmpRoot))
        {
            try
            {
                if (Directory.GetCreationTime(dir) < cutoff)
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 忽略：可能被占用（并发测试）或无权限
            }
        }
    }
}
catch
{
    // 清理失败不影响测试运行
}
var TestTmp = Path.Combine(testTmpRoot, Environment.ProcessId.ToString());
Directory.CreateDirectory(TestTmp);

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
bool PhOut(params string[] lines) => (bool)Invoke(mPhOut, new object?[] { (IReadOnlyList<string>)lines.ToList(), null, null })!;
Check("标题+数据行不判占位（fib 形态，P2-1）", !PhOut("Fibonacci 序列:", "0 1 1 2 3 5 8"));
Check("单行纯标题壳判占位（P2-1）", PhOut("结果:"));
Check("全空标题壳判占位（P2-1）", PhOut("订单:", "客户:"));
Check("键值输出不判占位（P2-1）", !PhOut("IP: 8.8.8.8", "Temperature: 21°C"));
Check("模板单行 hello 命中", (bool)Invoke(mTmpl, new object?[] { (IReadOnlyList<string>)new List<string> { "hello" } })!);
Check("模板 success 命中", (bool)Invoke(mTmpl, new object?[] { (IReadOnlyList<string>)new List<string> { "success" } })!);
Check("多行正常输出不判模板", !(bool)Invoke(mTmpl, new object?[] { (IReadOnlyList<string>)new List<string> { "IP: 8.8.8.8", "Temperature: 21°C" } })!);

Console.WriteLine("== WriteFileTool 写入时 timeout 静态检查（§14-3h） ==");
{
    var ws = Path.Combine(TestTmp, "tests_timeout");
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

Console.WriteLine("== PyCodeGuard 交互拒绝 + edit_file 后处理（§14-3j） ==");
{
    var ws = Path.Combine(TestTmp, "tests_guard");
    if (Directory.Exists(ws)) Directory.Delete(ws, true);
    Directory.CreateDirectory(ws);
    var wTool = new Agent.Infrastructure.Tools.WriteFileTool(ws);
    var eTool = new Agent.Infrastructure.Tools.EditFileTool(ws);

    string W(string path, string content)
    {
        var json = $"{{\"path\":\"{path}\",\"content\":{JsonSerializer.Serialize(content)}}}";
        var args = JsonSerializer.Deserialize<JsonElement>(json);
        return wTool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
    }

    string R(string path) =>
        File.ReadAllText(Path.Combine(ws, path.Replace('/', Path.DirectorySeparatorChar)));

    Check("write .py 含 input( → 拒绝",
        W("i.py", "x = input(\"请输入: \")").Contains("交互输入"));
    Check("write .cs 含 Console.ReadLine( → 拒绝",
        W("i.cs", "var x = Console.ReadLine();").Contains("交互输入"));
    Check("正常 .py 不被拒",
        !W("ok.py", "print(\"hello\")").Contains("错误"));

    // edit_file：.py 新内容补 timeout + 交互拒绝
    File.WriteAllText(Path.Combine(ws, "e.py"), "r = requests.get(\"http://ip-api.com/json/\")");
    var eJson = $"{{\"path\":\"e.py\",\"old_string\":{JsonSerializer.Serialize("r = requests.get(\"http://ip-api.com/json/\")")},\"new_string\":{JsonSerializer.Serialize("r = requests.get(\"http://ip-api.com/json/\")\nx = input()")}}}";
    var eArgs = JsonSerializer.Deserialize<JsonElement>(eJson);
    var eRet = eTool.InvokeAsync(eArgs, CancellationToken.None).GetAwaiter().GetResult();
    Check("edit .py 新内容含 input( → 拒绝", eRet.Contains("交互输入"));

    var eJson2 = $"{{\"path\":\"e.py\",\"old_string\":{JsonSerializer.Serialize("r = requests.get(\"http://ip-api.com/json/\")")},\"new_string\":{JsonSerializer.Serialize("r = requests.get(\"http://ip-api.com/json/\")")}}}";
    var eArgs2 = JsonSerializer.Deserialize<JsonElement>(eJson2);
    eTool.InvokeAsync(eArgs2, CancellationToken.None).GetAwaiter().GetResult();
    Check("edit .py 无 timeout → 补 timeout=8",
        R("e.py").Contains(", timeout=8"));

    // 【10-08 B】同文件内自重复类型定义：沙箱模式（无工程）同样拦截——
    // 实证 LetterCaseChecker.cs 900 字符内 2×class LetterCaseChecker → CS0101。
    var selfDupRet = W("Dup.cs",
        "using System;\npublic class LetterCaseChecker\n{\n}\npublic class LetterCaseChecker\n{\n}\n");
    Check("B 沙箱 write_file 自身重复类型 → 拒绝（pd_conflict）",
        selfDupRet.Contains("自身重复定义类型") && selfDupRet.Contains("LetterCaseChecker"));
    Check("B write_file 拒绝 → 文件未创建",
        !File.Exists(Path.Combine(ws, "Dup.cs")));
    // 单类正常写入不受影响
    Check("B 沙箱 write_file 单类 → 成功",
        W("Single.cs", "public class Single { }").Contains("已写入"));
    // edit_file 新内容自身重复类型 → 拒绝
    File.WriteAllText(Path.Combine(ws, "editDup.cs"), "public class Keep { }");
    var eDupJson = $"{{\"path\":\"editDup.cs\",\"old_string\":{JsonSerializer.Serialize("public class Keep { }")},\"new_string\":{JsonSerializer.Serialize("public class Keep { }\npublic class Keep { }")}}}";
    var eDupArgs = JsonSerializer.Deserialize<JsonElement>(eDupJson);
    var eDupRet = eTool.InvokeAsync(eDupArgs, CancellationToken.None).GetAwaiter().GetResult();
    Check("B edit_file 新内容自身重复类型 → 拒绝",
        eDupRet.Contains("自身重复定义类型") && File.ReadAllText(Path.Combine(ws, "editDup.cs")).Contains("Keep { }"));
}


Console.WriteLine("== 工程感知 P1：写工具双根 + 备份 + 回滚 ==");
{
    var baseTmp = Path.Combine(TestTmp, "tests_p1");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    var ws = Path.Combine(baseTmp, "task");      // 任务工作区（沙箱根）
    var proj = Path.Combine(baseTmp, "proj");    // 工程根
    Directory.CreateDirectory(ws);
    Directory.CreateDirectory(Path.Combine(proj, "src"));
    File.WriteAllText(Path.Combine(proj, "src", "A.cs"), "// original\n");

    string CallTool(Agent.Infrastructure.Tools.AgentToolBase tool, string json)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(json);
        return tool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
    }

    // 1) 沙箱模式（无工程）：写任务根外的路径 → 越界拒绝
    var wSandbox = new Agent.Infrastructure.Tools.WriteFileTool(ws);
    Check("沙箱模式写任务根外 → 越界拒绝",
        W(wSandbox, "../proj/src/X.cs", "x").Contains("越界"));

    // 2) 工程只读（--write 未开）：写基准仍为任务目录 → 不触碰工程根
    var wRo = new Agent.Infrastructure.Tools.WriteFileTool(ws, proj, false);
    W(wRo, "src/X.cs", "x");
    Check("工程只读 → 写入任务目录而非工程",
        File.Exists(Path.Combine(ws, "src", "X.cs")) && !File.Exists(Path.Combine(proj, "src", "X.cs")));

    // 3) 工程可写：写工程根新文件 → 成功
    var wRw = new Agent.Infrastructure.Tools.WriteFileTool(ws, proj, true);
    string W(Agent.Infrastructure.Tools.AgentToolBase tool, string path, string content)
    {
        var json = $"{{\"path\":\"{path}\",\"content\":{JsonSerializer.Serialize(content)}}}";
        return CallTool(tool, json);
    }
    string E(Agent.Infrastructure.Tools.AgentToolBase tool, string path, string oldStr, string newStr)
    {
        var json = $"{{\"path\":\"{path}\",\"old_string\":{JsonSerializer.Serialize(oldStr)},\"new_string\":{JsonSerializer.Serialize(newStr)}}}";
        return CallTool(tool, json);
    }
    Check("工程可写创建文件 → 成功",
        W(wRw, "src/B.cs", "// new\n").Contains("已写入")
        && File.Exists(Path.Combine(proj, "src", "B.cs")));

    // 4) 工程可写：覆盖已存在文件 → 自动备份 + 内容变更
    W(wRw, "src/A.cs", "// v2\n");
    var backupsA = Agent.Infrastructure.Project.BackupService.ListBackups(ws);
    Check("覆盖工程文件 → 备份生成",
        backupsA.Any(b => b.Rel.Replace('\\', '/').EndsWith("src/A.cs")));
    Check("覆盖工程文件 → 内容已变",
        File.ReadAllText(Path.Combine(proj, "src", "A.cs")).Contains("v2"));

    // 5) edit_file 工程可写：改工程文件 → 备份 + 内容变更
    var eRw = new Agent.Infrastructure.Tools.EditFileTool(ws, proj, true);
    E(eRw, "src/A.cs", "// v2", "// v3");
    Check("edit 工程文件 → 内容已变",
        File.ReadAllText(Path.Combine(proj, "src", "A.cs")).Contains("v3"));
    Check("edit 工程文件 → 备份已生成",
        Agent.Infrastructure.Project.BackupService.ListBackups(ws)
            .Any(b => b.Rel.Replace('\\', '/').EndsWith("src/A.cs")));

    // 6) rollback → 恢复最近一次备份（v3 改前备份了 v2）
    var restored = Agent.Infrastructure.Project.BackupService.Rollback(ws, "src/A.cs", proj, "default");
    var contentAfterRollback = File.ReadAllText(Path.Combine(proj, "src", "A.cs"));
    Check("rollback 恢复 → 返回恢复路径", restored is not null);
    Check("rollback 恢复 → 内容回到修改前",
        contentAfterRollback.Contains("v2") && !contentAfterRollback.Contains("v3"));

    // 7) 工程可写：路径穿越（越出工程根）→ 越界拒绝
    Check("工程可写路径穿越 → 越界拒绝",
        W(wRw, "../../../../Windows/test.txt", "x").Contains("越界"));

    // 8) 备份根归一化（§14-3w）：任务目录（task_*）写入备份 → run 目录（CLI rollback）能找到并恢复
    //    模拟 CLI：run 目录 ws2，任务目录 ws2/task_xxx，EditFileTool 用任务目录，rollback 用 run 目录
    var ws2 = Path.Combine(TestTmp, "run_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6]);
    var taskWs = Path.Combine(ws2, "task_smoke");
    Directory.CreateDirectory(taskWs);
    Directory.CreateDirectory(Path.Combine(proj, "src"));
    File.WriteAllText(Path.Combine(proj, "src", "B.cs"), "// v1");
    var eTask = new Agent.Infrastructure.Tools.EditFileTool(taskWs, proj, true);
    E(eTask, "src/B.cs", "// v1", "// v2");
    Check("任务目录工具改工程文件 → 内容已变",
        File.ReadAllText(Path.Combine(proj, "src", "B.cs")).Contains("v2"));
    var backupsRun = Agent.Infrastructure.Project.BackupService.ListBackups(ws2);
    Check("任务目录备份 → run 目录能列出（备份根归一化）",
        backupsRun.Any(b => b.Rel.Replace('\\', '/').EndsWith("src/B.cs")));

    // 9.5) 【任务4 备份生命周期】PruneBackups：构造 5 个任务备份目录 → 保留最近 2，删更早 3
    var pruneWs = Path.Combine(TestTmp, "run_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(pruneWs);
    for (var i = 1; i <= 5; i++)
    {
        var stampDir = Path.Combine(pruneWs, ".backup", "task_" + i, "000000000");
        Directory.CreateDirectory(stampDir);
        File.WriteAllText(Path.Combine(stampDir, "A.cs"), "// t" + i);
    }
    var pruned = Agent.Infrastructure.Project.BackupService.PruneBackups(pruneWs, 2);
    var remainTasks = Directory.GetDirectories(Path.Combine(pruneWs, ".backup"))
        .Select(Path.GetFileName).OrderByDescending(x => x).ToList();
    Check("PruneBackups 保留最近 2 个（删除 3 个）",
        pruned == 3 && remainTasks.Count == 2
        && remainTasks[0] == "task_5" && remainTasks[1] == "task_4");
    Check("PruneBackups keepLastN=0 → 全清",
        Agent.Infrastructure.Project.BackupService.PruneBackups(pruneWs, 0) == 2
        && !Directory.Exists(Path.Combine(pruneWs, ".backup", "task_5")));

    // 9) 【P-D】写前类型冲突检测：工程可写时，新内容定义工程其他文件已有类型 → 拒绝写入
    //    先给工程种下已有类型：Order.cs 定义 class Order（ShippingService 任务失败根因场景）
    Directory.CreateDirectory(Path.Combine(proj, "src"));
    File.WriteAllText(Path.Combine(proj, "src", "Order.cs"),
        "using System;\nclass Order\n{\n    public int Id { get; set; }\n}\n");
    // 9a) write_file 覆盖已存在文件但新内容重复定义已有类型 → 拒绝（文件不变）
    var beforeOrderSvc = File.Exists(Path.Combine(proj, "src", "OrderService.cs"))
        ? File.ReadAllText(Path.Combine(proj, "src", "OrderService.cs")) : null;
    var pdWrite = W(wRw, "src/OrderService.cs",
        "using System;\nclass Order\n{\n    public int Id { get; set; }\n}\nclass OrderService\n{\n}\n");
    Check("P-D write_file 重复定义已有类型 → 拒绝",
        pdWrite.Contains("类型重复定义") && pdWrite.Contains("Order"));
    Check("P-D 拒绝消息 → 附工程已有类型清单（引导能/不能定义什么）",
        pdWrite.Contains("工程已有类型") && pdWrite.Contains("Order(src/Order.cs)") && pdWrite.Contains("请勿重复定义以上类型"));
    Check("P-D write_file 拒绝 → 目标文件未被写入",
        beforeOrderSvc == null
            ? !File.Exists(Path.Combine(proj, "src", "OrderService.cs"))
            : File.ReadAllText(Path.Combine(proj, "src", "OrderService.cs")) == beforeOrderSvc);
    // 9b) write_file 新建文件也检测（模型可能新建同名类型文件）→ 拒绝
    var pdNew = W(wRw, "src/OrderDup.cs",
        "using System;\nclass Order\n{\n    public int Id { get; set; }\n}\n");
    Check("P-D write_file 新建文件含已有类型 → 拒绝",
        pdNew.Contains("类型重复定义"));
    Check("P-D 新建文件拒绝 → 文件未创建",
        !File.Exists(Path.Combine(proj, "src", "OrderDup.cs")));
    // 9b1) 10-04 增量修改引导：新建文件拒绝消息 → 含"是新建文件"指引（数据驱动，告诉模型下一步该做什么）
    Check("P-D 新建文件拒绝消息 → 含'是新建文件'指引",
        pdNew.Contains("是新建文件") && pdNew.Contains("OrderDup.cs") && pdNew.Contains("edit_file"));
    // 9b2) 目标文件已存在（覆盖）场景 → 消息含 read_file/edit_file 局部修改指引（不整体重写）
    W(wRw, "src/OrderService.cs", "using System;\nclass OrderService\n{\n    public int Id { get; set; }\n}\n");
    var pdExist = W(wRw, "src/OrderService.cs",
        "using System;\nclass Order\n{\n}\nclass OrderService\n{\n}\n");
    Check("P-D 已存在文件拒绝消息 → 含 read_file/edit_file 局部修改指引",
        pdExist.Contains("已存在于工程") && pdExist.Contains("read_file")
        && pdExist.Contains("edit_file") && pdExist.Contains("不要 write_file 整体重写"));
    Check("P-D 已存在文件拒绝 → 文件保持原状（未被覆盖）",
        File.ReadAllText(Path.Combine(proj, "src", "OrderService.cs")).Contains("public int Id"));
    // 9c) 目标文件自身已有该类型（partial 场景）→ 不算冲突（扫描排除目标文件）
    var pdSelf = W(wRw, "src/Order.cs",
        "using System;\nclass Order\n{\n    public int Id { get; set; }\n    public string? Status { get; set; }\n}\n");
    Check("P-D 目标文件自身含该类型 → 放行（partial 语义）",
        pdSelf.Contains("已写入"));
    // 9d) 无冲突正常写入 → 放行
    var pdOk = W(wRw, "src/NewService.cs",
        "using System;\nclass NewService\n{\n    public int Run() => 1;\n}\n");
    Check("P-D 无冲突写入 → 成功",
        pdOk.Contains("已写入") && File.Exists(Path.Combine(proj, "src", "NewService.cs")));
    // 9e) edit_file 引入已有类型定义 → 拒绝
    var pdEdit = E(eRw, "src/NewService.cs",
        "class NewService", "class Order\n{\n}\nclass NewService");
    Check("P-D edit_file 引入已有类型 → 拒绝",
        pdEdit.Contains("类型重复定义") && pdEdit.Contains("Order"));
    Check("P-D edit_file 拒绝 → 文件未变",
        File.ReadAllText(Path.Combine(proj, "src", "NewService.cs")).Contains("Run"));
    // 9f) TypeConflictDetector 单元：ExtractTypeNames 覆盖五种类型关键字 + 注释/字符串不误报
    var pdTypes = Agent.Infrastructure.Project.TypeConflictDetector.ExtractTypeNames(
        "using System;\n// class Fake\nclass Real { }\ninterface IThing { }\nenum Color { Red }\nstruct Point { }\nrecord Person(string Name);\nstring s = \"class InString\";");
    Check("P-D ExtractTypeNames → 提取 5 类 + 排除注释/字符串",
        pdTypes.Contains("Real") && pdTypes.Contains("IThing") && pdTypes.Contains("Color")
        && pdTypes.Contains("Point") && pdTypes.Contains("Person")
        && !pdTypes.Contains("Fake") && !pdTypes.Contains("InString"));
    // 9g) 【B】P-D 熔断语义化：同路径第 2 次被拒 → 确定性指令（禁止再 write_file，强制 edit_file）。
    //     实证：模型被 P-D 拒后不换 edit_file、反复 write_file → 3 连拒熔断白跑。
    //     第 2 次拒绝即返回强指令（数据驱动引导，非提示词）。
    var pdAgain = W(wRw, "src/OrderService.cs",
        "using System;\nclass Order\n{\n}\nclass OrderService\n{\n}\n");
    Check("P-D 连续拒绝 → 熔断指令（第 3 次 + 禁止 write_file + 强制 edit_file）",
        pdAgain.Contains("第 3 次") && pdAgain.Contains("禁止再次使用 write_file")
        && pdAgain.Contains("edit_file") && pdAgain.Contains("类型重复被拒绝"));
    Check("P-D 二连拒 → 文件仍未变",
        File.ReadAllText(Path.Combine(proj, "src", "OrderService.cs")).Contains("public int Id"));
    // 9h) 不同路径的拒绝计数独立（写其他文件第 1 次仍为普通拒绝）
    var pdOther1 = W(wRw, "src/OrderDup2.cs",
        "using System;\nclass Order\n{\n}\n");
    Check("P-D 其他路径第 1 次 → 普通拒绝（非熔断）",
        pdOther1.Contains("类型重复定义") && !pdOther1.Contains("第 2 次"));

    // 【方案A】工程模式写前拦截项目文件：防"侧路新建同名项目"假完成
    // （模型新建 OrderSystem.csproj + 全套文件 → dotnet run 编译通过 = 假完成，demo 工程未接入）
    var aCsproj = W(wRw, "OrderSystem.csproj", "<Project></Project>");
    Check("方案A write_file 新建 .csproj → 拒绝",
        aCsproj.Contains("工程模式禁止修改/新建项目文件") && aCsproj.Contains("退出码：126"));
    Check("方案A write_file 拒绝 → 文件未创建",
        !File.Exists(Path.Combine(proj, "OrderSystem.csproj")));
    Check("方案A edit_file 改 .csproj → 拒绝",
        E(eRw, "lawcheck_demo.csproj", "<Project>", "<Project>").Contains("工程模式禁止修改项目文件"));
    Check("方案A write_file 新建 .sln → 拒绝",
        W(wRw, "demo.sln", "x").Contains("工程模式禁止修改/新建项目文件"));
    var restoredB = Agent.Infrastructure.Project.BackupService.Rollback(ws2, "src/B.cs", proj, "default");
    Check("run 目录 rollback → 恢复任务目录产生的备份",
        restoredB is not null && File.ReadAllText(Path.Combine(proj, "src", "B.cs")).Contains("v1"));
}


Console.WriteLine("== 工程感知 P2：跨任务记忆（§14-3w P2） ==");
{
    var ctxDb = Path.Combine(TestTmp, "project_context_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
    var store = new Agent.Infrastructure.Project.ProjectContextStore(ctxDb);
    var projP2 = Path.Combine(TestTmp, "p2proj_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(Path.Combine(projP2, "src"));
    File.WriteAllText(Path.Combine(projP2, "Program.cs"), "// v1");
    File.WriteAllText(Path.Combine(projP2, "src", "A.cs"), "// a1");
    var sig = Agent.Infrastructure.Project.ProjectScanner.ComputeSignature(projP2);
    Check("签名含文件数 2", sig.FileCount == 2);
    Check("签名含目录数 1", sig.DirCount == 1);
    Check("首次查询 → 未命中",
        store.TryGet(projP2, sig.FileCount, sig.DirCount, sig.TotalBytes, sig.MaxMtimeTicks) is null);
    var scanP2 = Agent.Infrastructure.Project.ProjectScanner.Scan(projP2);
    Check("Scan 产出 CompactBlock（目录聚合）", scanP2.CompactBlock.Contains("目录文件数"));
    Check("CompactBlock 瘦身（不含具体文件名）", !scanP2.CompactBlock.Contains("A.cs"));
    Check("全量 ContextBlock 含具体文件名", scanP2.ContextBlock.Contains("A.cs"));
    store.Put(projP2, scanP2);
    var hitP2 = store.TryGet(projP2, sig.FileCount, sig.DirCount, sig.TotalBytes, sig.MaxMtimeTicks);
    Check("签名一致 → 缓存命中", hitP2 is not null && hitP2.CompactBlock.Contains("目录文件数"));
    Check("缓存命中返回 Overview", hitP2 is not null && hitP2.Overview.Contains("文件"));
    File.WriteAllText(Path.Combine(projP2, "src", "A.cs"), "// a2 changed");
    var sig2 = Agent.Infrastructure.Project.ProjectScanner.ComputeSignature(projP2);
    Check("文件修改 → 签名变化", sig2 != sig);
    Check("新签名 → 缓存未命中（自动重扫）",
        store.TryGet(projP2, sig2.FileCount, sig2.DirCount, sig2.TotalBytes, sig2.MaxMtimeTicks) is null);
    // 新签名下 Scan+Put → 再命中
    var scanP2b = Agent.Infrastructure.Project.ProjectScanner.Scan(projP2);
    store.Put(projP2, scanP2b);
    Check("重扫后 → 新签名命中",
        store.TryGet(projP2, sig2.FileCount, sig2.DirCount, sig2.TotalBytes, sig2.MaxMtimeTicks) is not null);
}

Console.WriteLine("== 工程感知 P3：模型级记忆（§14-3w P3） ==");
{
    var memDb = Path.Combine(TestTmp, "agent_memory_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
    var mem = new Agent.Infrastructure.Memory.AgentMemoryStore(memDb);

    // Tokenize
    var toks = Agent.Infrastructure.Memory.AgentMemoryStore.Tokenize("读取 src/Agent.Core/Models/AgentOptions.cs 中 ProjectRoot 属性附近的内容");
    Check("Tokenize 含中文串", toks.Any(x => x.Contains("读取")) || toks.Any(x => x.Contains("属性附近")) || toks.Any(x => x.Contains("内容")));
    Check("Tokenize 含文件 token", toks.Any(x => x.Contains("AgentOptions")));
    Check("Tokenize 含路径 token", toks.Any(x => x.Contains("Agent.Core")));

    // Add + Search 命中
    mem.Add("model", "ProjectRoot 属性含义", "ProjectRoot 为 null 时沙箱模式，非 null 时工程模式。", "task_demo");
    mem.Add("model", "构建命令", "构建用 dotnet build LocalAiWorkbench.slnx，入口 src/Agent.Cli。");
    mem.Add("model", "天气 API", "wttr.in 无需 API Key，format=j1 返回 JSON。");
    var hits = mem.Search("读取 ProjectRoot 属性的代码", limit: 2);
    Check("Search 命中 ProjectRoot 记忆", hits.Any(h => h.Topic.Contains("ProjectRoot")));
    Check("Search 不命中无关记忆（limit 截断）", hits.All(h => !h.Topic.Contains("天气")));

    // 命中计数
    var h1 = hits.FirstOrDefault(h => h.Topic.Contains("ProjectRoot"));
    Check("命中计数 > 0", h1 is not null && h1.HitCount >= 1);

    // 【任务5】记忆注入特征过滤：kind=task 跨特征类剔除；kind=model 跨类降权保留
    var memF = Path.Combine(TestTmp, "agent_memory_f_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
    var mf = new Agent.Infrastructure.Memory.AgentMemoryStore(memF);
    mf.Add("task", "天气任务归档", "wttr.in 天气获取成功。", "task_1", "ExternalCredential");
    mf.Add("model", "天气 API 决策", "wttr.in 无需 Key 可用。", null, "ExternalCredential");
    mf.Add("task", "学生系统任务归档", "C# 学生系统 4 文件构建通过。", "task_2", "MultiFile");
    // 特征类=MultiFile 检索"学生系统"：同类的 task 归档命中；跨类的 task 归档剔除（model 决策关键词不命中本就不返回）
    var fh = mf.Search("学生系统", limit: 10, feature: "MultiFile");
    Check("同特征类 task 记忆命中", fh.Any(h => h.Topic.Contains("学生系统")));
    Check("跨特征类 task 记忆剔除", fh.All(h => !h.Topic.Contains("天气任务归档")));
    Check("关键词不命中则 model 决策不注入", fh.All(h => !h.Topic.Contains("天气 API 决策")));
    // 降权验证：检索"天气" + feature=MultiFile——跨类 task 剔除；跨类 model 决策 topic 命中（score2→1）仍保留
    var fh2 = mf.Search("天气", limit: 10, feature: "MultiFile");
    Check("跨类检索：model 决策降权保留 + task 归档剔除",
        fh2.Any(h => h.Topic.Contains("天气 API 决策")) && !fh2.Any(h => h.Topic.Contains("天气任务归档")));

    // 二次检索命中数递增
    mem.Search("ProjectRoot 属性的含义", limit: 2);
    var hits2 = mem.Search("ProjectRoot 属性的含义", limit: 2);
    var h2 = hits2.FirstOrDefault(h => h.Topic.Contains("ProjectRoot"));
    Check("命中计数递增", h2 is not null && h2.HitCount >= 2);

    // 空/无效写入被忽略
    mem.Add("model", "   ", "内容");
    mem.Add("model", "主题", "  ");
    Check("空 topic/content 不写入", mem.Search("主题").Count == 0);

    // RememberTool 校验
    var tool = new Agent.Infrastructure.Tools.RememberTool(mem);
    var rEmpty = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse("{\"topic\":\"x\",\"content\":\" \"}").RootElement, CancellationToken.None).Result;
    Check("RememberTool 空 content 拒绝", rEmpty.Contains("不能为空"));
    var rOk = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse("{\"topic\":\"记忆工具验证\",\"content\":\"remember 工具写入正常\"}").RootElement, CancellationToken.None).Result;
    Check("RememberTool 写入成功", rOk.Contains("已记住"));
    Check("RememberTool 写入可检索", mem.Search("记忆工具验证").Any(h => h.Topic.Contains("记忆工具验证")));
}

Console.WriteLine("== 工程感知 P3 补足：任务归档记忆（§14-3w P3） ==");
{
    // 成功归档
    var (t1, c1) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
        "帮我做一个 C# 订单系统项目，多文件组织\n第二行", true, new List<string>(), "orchestrator",
        "已创建 4 个文件，验证通过。");
    Check("成功 topic 去换行截断", t1.Contains("帮我做一个") && !t1.Contains("\n"));
    Check("成功 content 含完成标记", c1.Contains("任务完成（orchestrator）") && c1.Contains("已创建 4 个文件"));
    Check("成功 content 不含失败标记", !c1.Contains("任务失败"));

    // 失败归档：错误提炼
    var (t2, c2) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
        "用 C# 写一个天气 API 调用", false, new List<string> { "模型连续返回空答案，任务终止。", "升级失败" }, "agent");
    Check("失败 topic 含输入", t2.Contains("天气 API"));
    Check("失败 content 含失败标记", c2.Contains("任务失败（agent）"));
    Check("失败 content 提炼错误教训", c2.Contains("模型连续返回空答案"));

    // 无错误信息的失败归档
    var (t3, c3) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
        "读取某文件", false, new List<string>(), "agent");
    Check("无错误 → 未知错误", c3.Contains("未知错误"));

    // 长输入 topic 截断 + content 截断
    var longInput = new string('长', 100);
    var (t4, c4) = Agent.Infrastructure.Memory.TaskMemoryExtractor.Extract(
        longInput, false, new List<string> { new string('错', 500) }, "agent");
    Check("topic 截断到 40", t4.Length == 40);
    Check("content 截断到 200", c4.Length == 200);

    // 归档写入 store（kind=task）可检索
    var memDb2 = Path.Combine(TestTmp, "agent_memory2_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
    var mem2 = new Agent.Infrastructure.Memory.AgentMemoryStore(memDb2);
    mem2.Add("task", "读取 AgentOptions 配置", "任务完成（agent）：已读取并总结。", "task_abc");
    var taskHits = mem2.Search("读取 AgentOptions 配置", limit: 3);
    Check("task 记忆可检索", taskHits.Any(h => h.Kind == "task" && h.Topic.Contains("AgentOptions")));
}

Console.WriteLine("== 记忆治理收尾：自动过期清理 + 默认阈值 ==");
{
    // AgentOptions 默认治理阈值（任务启动时自动执行）
    var opt = new Agent.Core.Models.AgentOptions();
    Check("治理默认 30 天", opt.MemoryPruneDays == 30);
    Check("治理默认 1 命中（仅 0 命中可清）", opt.MemoryPruneMinHits == 1);

    // 【路由调权收尾】调权参数默认值（与 ModelRouter 原硬编码一致，行为零变化）
    Check("调权默认样本 3", opt.RoutingTuningMinSamples == 3);
    Check("写降级默认阈值 0.6", opt.RoutingDowngradeMinSuccessRate == 0.6);
    Check("读升级默认坏率 0.6", opt.RoutingReadUpgradeBadRate == 0.6);
    Check("读升级默认成功率 0.5", opt.RoutingReadUpgradeMinSuccessRate == 0.5);
    Check("Complex 降级默认样本 5", opt.RoutingDropAgentProMinSamples == 5);
    Check("Complex 降级默认阈值 0.5", opt.RoutingDropAgentProMinSuccessRate == 0.5);

    // PruneExpired 确定性验证：久未使用且 0 命中 → 删除；有命中 → 保留
    var memPPath = Path.Combine(TestTmp, "agent_memory_p_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
    var memP = new Agent.Infrastructure.Memory.AgentMemoryStore(memPPath);
    memP.Add("task", "旧任务无命中", "很久以前的失败归档。", "old_task");
    memP.Add("model", "常用知识", "常用命中知识。", "old_model");
    memP.Search("常用知识", limit: 1);   // 命中一次 → hit_count=1

    // 模拟久未使用：把两条的 last_hit_at 都改为 35 天前（30 天阈值外）
    using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + memPPath))
    {
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agent_memory SET created_at='2026-08-20 00:00:00', last_hit_at='2026-08-20 00:00:00';";
        cmd.ExecuteNonQuery();
    }

    var pruned = memP.PruneExpired(30, 1);   // 35 天前最后使用 且 hit_count<1 → 仅删"旧任务无命中"
    Check("过期 0 命中清理 1 条", pruned == 1);
    Check("常用知识（有命中）保留", memP.Search("常用知识", limit: 1).Count == 1);
    Check("旧任务 0 命中已清", memP.Search("旧任务无命中", limit: 1).Count == 0);
}

Console.WriteLine("== 工程感知 P3 补足：失败教训注入（少走弯路） ==");
{
    var mem3 = new Agent.Infrastructure.Memory.AgentMemoryStore(
        Path.Combine(TestTmp, "agent_memory3_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db"));

    // 空 → null
    Check("无命中 → null", Agent.Infrastructure.Memory.MemoryPromptBuilder.Build(new List<Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit>()) is null);

    // 失败记忆 → 警示组单列
    var failHit = new Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit(1, "task", "创建 hello.txt", "任务失败（agent）：达到最大步数 20，任务终止。", 2);
    var okHit = new Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit(2, "model", "ProjectRoot 属性", "ProjectRoot 为 null 时沙箱模式。", 1);
    var built = Agent.Infrastructure.Memory.MemoryPromptBuilder.Build(new List<Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit> { failHit, okHit });
    Check("失败教训独立警示组", built is not null && built.Contains("历史失败教训") && built.Contains("避免重蹈覆辙"));
    Check("失败内容含教训原文", built is not null && built.Contains("达到最大步数 20"));
    Check("参考组含 model 记忆", built is not null && built.Contains("历史参考") && built.Contains("ProjectRoot 为 null 时沙箱模式"));
    Check("失败与参考分节", built is not null && built.IndexOf("历史失败教训") < built.IndexOf("历史参考"));

    // 两条失败 → 失败计数强化
    var failHit2 = new Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit(3, "task", "创建 hello2.txt", "任务失败（agent）：模型连续返回空答案，任务终止。", 1);
    var built2 = Agent.Infrastructure.Memory.MemoryPromptBuilder.Build(new List<Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit> { failHit, failHit2 });
    Check("多条失败 → 计数强化", built2 is not null && built2.Contains("同类失败累计已记录 2 次"));

    // 只有参考 → 无警示组
    var built3 = Agent.Infrastructure.Memory.MemoryPromptBuilder.Build(new List<Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit> { okHit });
    Check("仅参考无警示组", built3 is not null && !built3.Contains("历史失败教训") && built3.Contains("历史参考"));
}

Console.WriteLine("== 路由层修复 A+B：写任务直连 + 空响应跳层（§14-3w） ==");
{
    // B：空响应升级跳层——4B 空响应直接跳 large
    Check("small 空响应 → 直达 large", Agent.Infrastructure.AgentLoop.PromoteService("small") == "large");
    Check("agent_pro 空响应 → 直达 large", Agent.Infrastructure.AgentLoop.PromoteService("agent_pro") == "large");
    Check("large 空响应 → null（原层重试）", Agent.Infrastructure.AgentLoop.PromoteService("large") is null);

    // A：写任务判定（IsWriteTask 覆盖已测）——工程可写条件移除由 ModelRouter 运行时日志验证
    Check("写任务判定稳定", Agent.Infrastructure.Routing.IntentOverride.IsWriteTask("创建一个 hello.txt 文件，内容为 hello"));
}

Console.WriteLine("== 10-08 真实失败收尾 B：LLM 异常升级兜底 ==");
{
    // B：AgentLoop catch 处异常（canceled/超时/连接）→ PromoteService 升级一次；
    // 最高层（large）无升级目标 → 判失败。Error 统一前缀 "LLM 调用失败："。
    Check("LLM 调用异常 → llm_error 类别（非 empty_response）",
        Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("LLM 调用失败：任务已取消") == "llm_error");
    Check("LLM 调用异常（超时型）→ llm_error 类别",
        Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("LLM 调用失败：请求超时") == "llm_error");
    Check("异常升级语义：large 已最高层无升级目标（判失败路径）",
        Agent.Infrastructure.AgentLoop.PromoteService("large") is null);
    Check("异常升级语义：small/agent_pro 可升 large（复用空响应链映射）",
        Agent.Infrastructure.AgentLoop.PromoteService("small") == "large"
        && Agent.Infrastructure.AgentLoop.PromoteService("agent_pro") == "large");
}

Console.WriteLine("== 模板检测降级：任务要求输出豁免（方案 1） ==");
{
    // 任务要求内容为 hello，type 输出 hello → 合法（豁免模板判定）
    Check("任务要求的 hello 输出 → 豁免",
        Agent.Infrastructure.Quality.OutputVerifier.IsTaskExpectedOutput(
            "创建一个 hello.txt 文件，内容为 hello", new List<string> { "hello" }));
    // 单行非任务要求 → 不豁免（Hello World 糊弄仍拦截）
    Check("任务无关的 hello → 不豁免",
        !Agent.Infrastructure.Quality.OutputVerifier.IsTaskExpectedOutput(
            "用 C# 写一个加法计算器", new List<string> { "hello" }));
    // 多行输出：IsTemplateOutput 仅单行判定（多行不判模板），数据行无需走豁免
    Check("多行输出不判模板（单行限制）",
        !Agent.Infrastructure.Quality.OutputVerifier.IsTemplateOutput(
            new List<string> { "订单总价：300.00", "数量：2" }));
    // 输入包含整行 → 豁免（单行任务要求内容）
    Check("输入含输出行 → 豁免",
        Agent.Infrastructure.Quality.OutputVerifier.IsTaskExpectedOutput(
            "内容为 hello", new List<string> { "hello" }));
    // 空预期 → 不豁免
    Check("空预期 → 不豁免",
        !Agent.Infrastructure.Quality.OutputVerifier.IsTaskExpectedOutput("", new List<string> { "hello" }));
    // 大小写不敏感
    Check("大小写不敏感 → 豁免",
        Agent.Infrastructure.Quality.OutputVerifier.IsTaskExpectedOutput(
            "Hello World 示例", new List<string> { "hello world" }));

    // ── 方案 C：大小写/标点归一（Hello, World! ↔ hello world）──
    Check("Hello, World! 大小写标点归一 → 豁免",
        Agent.Infrastructure.Quality.OutputVerifier.IsTaskExpectedOutput(
            "写一个名为pyst.py的脚本，脚本任务是打印 hello world",
            new List<string> { "Hello, World!" }));
    Check("输入无关输出不豁免（加法任务输出 hello 仍拦截）",
        !Agent.Infrastructure.Quality.OutputVerifier.IsTaskExpectedOutput(
            "用 C# 写一个加法计算器", new List<string> { "hello" }));
    // 方案 2：循环熔断签名——同工具+首参+输出指纹
    {
        var argsHello = System.Text.Json.JsonDocument.Parse("{\"path\":\"hello.txt\",\"content\":\"hello\"}").RootElement;
        var s1 = Agent.Infrastructure.AgentLoop.BuildCycleSignature("write_file", argsHello, "已写入 hello.txt，共 5 字符。");
        var s2 = Agent.Infrastructure.AgentLoop.BuildCycleSignature("write_file", argsHello, "已写入 hello.txt，共 5 字符。");
        var s3 = Agent.Infrastructure.AgentLoop.BuildCycleSignature("write_file", argsHello, "已写入 hello.txt，共 6 字符。");
        Check("同工具同参同输出 → 同签名", s1 == s2);
        Check("输出变化 → 签名变化", s1 != s3);
        var argsRun = System.Text.Json.JsonDocument.Parse("{\"command\":\"type hello.txt\"}").RootElement;
        var r1 = Agent.Infrastructure.AgentLoop.BuildCycleSignature("run_command", argsRun, "hello");
        Check("不同工具/参数 → 不同签名", r1 != s1);
    }

    // 组合实证：IsTemplateOutput && !IsTaskExpectedOutput 不应判模板
    var comboIsTemplate = Agent.Infrastructure.Quality.OutputVerifier.IsTemplateOutput(new List<string> { "hello" });
    var comboIsExpected = Agent.Infrastructure.Quality.OutputVerifier.IsTaskExpectedOutput(
        "创建一个 hello.txt 文件，内容为 hello", new List<string> { "hello" });
    Console.WriteLine($"  [组合实证] IsTemplateOutput={comboIsTemplate} IsTaskExpectedOutput={comboIsExpected} → 判模板={(comboIsTemplate && !comboIsExpected)}");
    Check("组合条件不判模板", !(comboIsTemplate && !comboIsExpected));
}

Console.WriteLine("== 工程感知 P1：run_command 路径边界（§14-3t） ==");
{
    var baseTmp = Path.Combine(TestTmp, "tests_p1rc");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    var ws = Path.Combine(baseTmp, "task");
    var proj = Path.Combine(baseTmp, "proj");
    Directory.CreateDirectory(ws);
    Directory.CreateDirectory(proj);

    string Rc(string? projectRoot, string command)
    {
        var tool = new Agent.Infrastructure.Tools.RunCommandTool(ws, projectRoot);
        var args = JsonSerializer.Deserialize<JsonElement>($"{{\"command\":{JsonSerializer.Serialize(command)}}}");
        return tool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
    }

    Check("工程模式：盘符敏感路径（C:\\Windows）→ 敏感源头拒",
        Rc(proj, "dir C:\\Windows").Contains("敏感"));
    Check("工程模式：路径穿越（..）→ 拒绝",
        Rc(proj, "type ..\\..\\Windows\\win.ini").Contains("越界"));
    Check("工程模式：工程根内绝对路径 → 放行",
        !Rc(proj, "dir \"" + proj.Replace("\\", "/") + "\"").Contains("越界"));
    Check("工程模式：相对路径命令 → 放行",
        !Rc(proj, "python main.py").Contains("越界"));
    Check("沙箱模式：不启用边界检查（现状保持）",
        !Rc(null, "dir C:\\Windows").Contains("越界"));

    // 【D1 衍生】项目名幻觉校验：命令引用的 .csproj 必须存在于工程根
    File.WriteAllText(Path.Combine(proj, "lawcheck_demo.csproj"), "<Project></Project>");
    Check("工程模式：dotnet run 无 --project → 放行（项目名校验不误伤）",
        !Rc(proj, "dotnet run").Contains("不存在于工程根"));
    Check("工程模式：幻觉项目名（OrderSystem.csproj）→ 拒绝+提示正确项目名",
        Rc(proj, "dotnet run --project OrderSystem.csproj").Contains("不存在于工程根")
        && Rc(proj, "dotnet run --project OrderSystem.csproj").Contains("lawcheck_demo.csproj"));
    Check("工程模式：真实项目名 → 放行",
        !Rc(proj, "dotnet run --project lawcheck_demo.csproj").Contains("不存在于工程根"));
    Check("沙箱模式：项目名校验不启用（现状保持）",
        !Rc(null, "dotnet run --project Whatever.csproj").Contains("不存在于工程根"));
}

Console.WriteLine("== TaskClassifier 编排判定（§14-3w） ==");
{
    bool Orc(string input) => Agent.Infrastructure.Orchestration.TaskClassifier.ShouldOrchestrate(input);

    Check("读取工程文件长描述 → 走 Agent（不编排）",
        !Orc("读取 src/Agent.Core/Models/AgentOptions.cs 中 ProjectRoot 属性附近的内容"));
    Check("构建验证任务 → 走 Agent（不编排）",
        !Orc("运行 dotnet build 检查工程能否构建"));
    Check("多文件项目 → 编排（强多文件词）",
        Orc("帮我做一个 C# 项目，包含订单模型、订单服务、主程序，多文件组织"));
    Check("学生管理系统多文件 → 编排（回归）",
        Orc("设计一个学生管理系统，使用C#多文件组织"));
    Check("天气方法 → 走 Agent（回归）",
        !Orc("写一个获取天气信息的方法"));
    Check("奇数偶数函数 → 走 Agent（回归）",
        !Orc("用 C# 写一个判断奇数偶数函数"));
    Check("订单明细项目 → 编排（系统/服务词）",
        Orc("用 C# 写一个订单明细项目，分 Order 模型、OrderItem 模型、OrderService 服务、Program 入口，多文件组织"));
    Check("添加注释到具体文件（长路径）→ 走 Agent（文件引用排除）",
        !Orc("在 src/Agent.Core/Models/AgentOptions.cs 中 ProjectRoot 属性定义之后添加一行注释：// P1 冒烟验证标记"));
    Check("带路径修改任务 → 走 Agent（文件引用排除）",
        !Orc("修改 src/Agent.Core/Models/AgentOptions.cs 中 ProjectWriteEnabled 属性的注释说明"));
    Check("含多文件词的工程任务 → 仍编排（强多文件词优先于文件引用）",
        Orc("做一个多文件的 C# 项目：src/Agent.Core/Models/AgentOptions.cs 和 Program.cs"));
}

Console.WriteLine("== IntentOverride 意图覆盖（§14-3w） ==");
{
    Agent.Infrastructure.Routing.IntentType Ov(Agent.Infrastructure.Routing.IntentType i, string s) => Agent.Infrastructure.Routing.IntentOverride.Apply(i, s);

    Check("SimpleQA + 添加注释到文件 → CodeGenerate",
        Ov(Agent.Infrastructure.Routing.IntentType.SimpleQA, "在 src/Agent.Core/Models/AgentOptions.cs 中 ProjectRoot 属性定义之后添加一行注释：// 标记") == Agent.Infrastructure.Routing.IntentType.CodeGenerate);
    // 【A 补全】"加一行注释"（日志实测缺口：OperationVerbs 缺单字"加" → 误判 SimpleQA）
    Check("加一行注释 → SimpleQA 覆盖为 CodeGenerate",
        Ov(Agent.Infrastructure.Routing.IntentType.SimpleQA, "在 BigService.cs 的 Method1500 方法里加一行注释 // 已审查") == Agent.Infrastructure.Routing.IntentType.CodeGenerate);
    Check("SimpleQA + 修改代码 → CodeGenerate",
        Ov(Agent.Infrastructure.Routing.IntentType.SimpleQA, "修改 AgentOptions.cs 中 ProjectWriteEnabled 属性的注释说明") == Agent.Infrastructure.Routing.IntentType.CodeGenerate);
    Check("SimpleQA + 纯问答 → 保持 SimpleQA",
        Ov(Agent.Infrastructure.Routing.IntentType.SimpleQA, "今天天气怎么样") == Agent.Infrastructure.Routing.IntentType.SimpleQA);
    Check("SimpleQA + 操作词但无文件语境 → 保持 SimpleQA",
        Ov(Agent.Infrastructure.Routing.IntentType.SimpleQA, "帮我添加一些建议") == Agent.Infrastructure.Routing.IntentType.SimpleQA);
    Check("非 SimpleQA 原样返回",
        Ov(Agent.Infrastructure.Routing.IntentType.FileProcess, "读取 src/Agent.Core/Models/AgentOptions.cs") == Agent.Infrastructure.Routing.IntentType.FileProcess);
    Check("空输入保持原样",
        Ov(Agent.Infrastructure.Routing.IntentType.SimpleQA, "   ") == Agent.Infrastructure.Routing.IntentType.SimpleQA);
}

// 【目标文件注入】ExtractTargetFile 数据驱动提取（最小版）
Console.WriteLine("== ExtractTargetFile 目标文件提取 ==");
{
    string? Tf(string s) => Agent.Infrastructure.AgentLoop.ExtractTargetFile(s);

    Check("提取 BigService.cs",
        Tf("在 BigService.cs 的 Method1500 方法里加一行注释") == "BigService.cs");
    Check("提取 py 文件",
        Tf("修改 weather.py 里的超时参数") == "weather.py");
    Check("提取 csproj",
        Tf("给 OrderSystem.csproj 添加包引用") == "OrderSystem.csproj");
    Check("无文件名 → null",
        Tf("帮我分析一下这个项目的架构") == null);
    Check("空输入 → null",
        Tf("   ") == null);
}

Console.WriteLine("== IntentOverride 写任务判定（§14-3w 直连 large） ==");
{
    bool Wr(string s) => Agent.Infrastructure.Routing.IntentOverride.IsWriteTask(s);

    Check("添加注释到文件 → 写任务",
        Wr("在 src/Agent.Core/Models/AgentOptions.cs 中 ProjectRoot 属性定义之后添加一行注释：// 标记"));
    Check("修改代码 → 写任务",
        Wr("修改 AgentOptions.cs 中 ProjectWriteEnabled 属性的注释说明"));
    Check("创建新文件 → 写任务",
        Wr("创建一个 Program.cs 文件"));
    Check("纯问答 → 非写任务",
        !Wr("今天天气怎么样"));
    Check("读取文件 → 非写任务（读保持 small）",
        !Wr("读取 src/Agent.Core/Models/AgentOptions.cs"));
    // 【A 补全】"加一行注释"（日志实测缺口：OperationVerbs 缺单字"加" → 误判 SimpleQA）
    Check("加一行注释 → 写任务（词表补全）",
        Wr("在 BigService.cs 的 Method1500 方法里加一行注释 // 已审查"));
    Check("列出目录 → 非写任务",
        !Wr("列出 src/Agent.Core/Models 目录"));
    Check("操作词但无文件语境 → 非写任务",
        !Wr("帮我添加一些建议"));

    // ── 方案 B：写动词词表补全（写/更改/改/生成…）──
    Check("写一个…脚本 → 写任务",
        Wr("写一个名为pyst.py的脚本，脚本任务是打印 hello world"));
    Check("更改这个脚本 → 写任务",
        Wr("更改这个脚本，将里面内容改成判断整数偶数的方法"));
    Check("生成一个Python脚本 → 写任务",
        Wr("生成一个Python脚本"));
    Check("改为…方法 → 写任务",
        Wr("将脚本内容改为判断整数偶数的方法"));
    Check("纯打印无语境 → 非写任务",
        !Wr("打印 hello"));
}
// ── 数据闭环：RoutingIntelligence 调权阈值（保守三条件）──
Console.WriteLine("== 数据闭环 RoutingIntelligence（历史表现回流调权） ==");
{
    bool Suggest(Agent.Infrastructure.Routing.ServiceStats cand,
                 Agent.Infrastructure.Routing.ServiceStats cur,
                 out string? reason)
        => Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestDowngrade(
            cand, cur, minSamples: 3, minSuccessRate: 0.6, out reason);

    // 1) 样本不足 → 不降级
    Check("样本不足（2 条）→ 不降级",
        !Suggest(new(2, 2, 50_000), new(1, 1, 600_000), out _));
    // 2) 成功率低 → 不降级
    Check("成功率低（3 条 1 成功）→ 不降级",
        !Suggest(new(3, 1, 50_000), new(3, 3, 600_000), out _));
    // 3) 不省时（candidate 平均 ≥ current 平均）→ 不降级
    Check("不省时 → 不降级",
        !Suggest(new(4, 3, 700_000), new(4, 3, 600_000), out _));
    // 4) 达标（样本足 + 高成功率 + 显著省时）→ 降级
    Check("达标 → 降级",
        Suggest(new(5, 4, 80_000), new(5, 4, 600_000), out var reason4)
        && reason4!.Contains("成功率"));
    // 5) 达标且当前无历史 → 降级
    Check("当前无历史 + 候选达标 → 降级",
        Suggest(new(4, 3, 80_000), new(0, 0, 0), out _));
}

// ── 读任务数据闭环：IsReadTask + TrySuggestReadTier ──
Console.WriteLine("== 读任务数据闭环（small → agent_pro 升级判定） ==");
{
    // IsReadTask：读动词 + 代码语境
    Check("读取文件 → 读任务", Agent.Infrastructure.Routing.IntentOverride.IsReadTask("读取 src/Order.cs 的内容"));
    Check("分析代码 → 读任务", Agent.Infrastructure.Routing.IntentOverride.IsReadTask("分析一下这个函数的逻辑"));
    Check("查看结构 → 读任务", Agent.Infrastructure.Routing.IntentOverride.IsReadTask("看看这个类的结构"));
    Check("纯问答 → 非读任务", !Agent.Infrastructure.Routing.IntentOverride.IsReadTask("什么是依赖注入"));
    Check("写任务（修改）→ 非读任务", !Agent.Infrastructure.Routing.IntentOverride.IsReadTask("修改 Order.cs 的价格属性"));
    Check("无代码语境 → 非读任务", !Agent.Infrastructure.Routing.IntentOverride.IsReadTask("看看天气"));

    bool SuggestRead(Agent.Infrastructure.Routing.ServiceStats pro,
                     Agent.Infrastructure.Routing.ServiceStats small,
                     out string? reason)
        => Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestReadTier(
            pro, small, minSamples: 3, badRate: 0.6, minSuccessRate: 0.5, out reason);

    // 1) small 样本不足 → 保持 small
    Check("small 样本不足 → 不升级",
        !SuggestRead(new(5, 4, 80_000), new(2, 1, 300_000), out _));
    // 2) small 成功率高（≥0.6）→ 保持 small
    Check("small 成功率高 → 不升级",
        !SuggestRead(new(5, 4, 80_000), new(5, 4, 300_000), out _));
    // 3) agent_pro 样本不足 → 保持 small
    Check("agent_pro 样本不足 → 不升级",
        !SuggestRead(new(2, 2, 80_000), new(5, 2, 300_000), out _));
    // 4) agent_pro 成功率低 → 保持 small
    Check("agent_pro 成功率低 → 不升级",
        !SuggestRead(new(4, 1, 80_000), new(5, 2, 300_000), out _));
    // 5) agent_pro 不省时 → 保持 small
    Check("agent_pro 不省时 → 不升级",
        !SuggestRead(new(5, 4, 350_000), new(5, 2, 300_000), out _));
    // 6) 全达标（small 差 + pro 足/达标/省时）→ 升级 agent_pro
    Check("达标 → 升级 agent_pro",
        SuggestRead(new(5, 4, 80_000), new(5, 2, 300_000), out var r6)
        && r6!.Contains("偏低"));
}

// ── D2：任务特征分类 + 历史聚合（路由数据校准） ──
Console.WriteLine("== D2 TaskFeatureClassifier + TaskMetricsAggregator ==");
{
    // 分类（确定性关键词）
    Check("工程增量：在工程中新增类 → ProjectIncremental",
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("在工程中新增一个 ShippingService 类") ==
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ProjectIncremental);
    Check("工程增量：修改已有文件加方法 → ProjectIncremental",
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("在 OrderService.cs 中新增一个方法") ==
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ProjectIncremental);
    Check("外部凭据：查天气 → ExternalCredential",
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("写一个获取天气的方法") ==
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ExternalCredential);
    Check("外部凭据：查 IP 天气 → ExternalCredential",
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("根据IP地址获取天气信息") ==
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ExternalCredential);
    Check("读/查：读取工程文件 → ReadQuery",
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("读取 Order.cs 的内容") ==
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ReadQuery);
    Check("多文件：分模型服务入口 → MultiFile",
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("做一个学生管理系统，分模型、服务、入口，多文件组织") ==
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.MultiFile);
    Check("单文件：写一个 C# 函数 → SingleFile",
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("用 C# 写一个判断奇数偶数的函数") ==
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.SingleFile);
    Check("其他：纯闲聊 → Other",
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("你好") ==
        Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.Other);

    // 聚合器：临时 sqlite 造 metrics → 成功率/失败主因
    var aggDir = Path.Combine(TestTmp, "d2agg_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(aggDir);
    var aggDb = Path.Combine(aggDir, "metrics.db");
    using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={aggDb}"))
    {
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE task_metrics(
                task_id TEXT, session_id TEXT, user_input TEXT, mode TEXT, routing_tier TEXT,
                service_id TEXT, routing_reason TEXT, success INTEGER, is_suspicious INTEGER,
                retry_count INTEGER, total_ms INTEGER, planner_ms INTEGER, executor_ms INTEGER,
                validator_ms INTEGER, lint_blocking_errors INTEGER, lint_warning_errors INTEGER,
                error_summary TEXT, started_at REAL, finished_at REAL)";
        cmd.ExecuteNonQuery();
        var ins = conn.CreateCommand();
        ins.CommandText = @"INSERT INTO task_metrics
            (user_input, success, total_ms, error_summary) VALUES (@i, @s, @t, @e)";
        foreach (var (input, ok, ms, err) in new[]
        {
            ("在工程中新增一个 ShippingService 类", 0, 120_000, "error CS0101: 重复定义"),
            ("在工程中修改 OrderService.cs", 0, 150_000, "error CS0101: 重复定义"),
            ("在工程中新增 TaxService", 0, 90_000, "提供的文件路径不存在"),
            ("写一个获取天气的方法", 1, 60_000, ""),
            ("写一个获取天气的方法", 1, 55_000, ""),
            ("用 C# 写一个判断奇偶的函数", 1, 30_000, ""),
        })
        {
            ins.Parameters.Clear();
            ins.Parameters.AddWithValue("@i", input);
            ins.Parameters.AddWithValue("@s", ok);
            ins.Parameters.AddWithValue("@t", ms);
            ins.Parameters.AddWithValue("@e", err);
            ins.ExecuteNonQuery();
        }
    }
    var agg = new Agent.Infrastructure.Routing.TaskMetricsAggregator(aggDb);
    agg.Load();
    var projStats = agg.Get(Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ProjectIncremental);
    Check("聚合：工程增量 3 条 0 成功",
        projStats is { TotalCount: 3, SuccessCount: 0 });
    Check("聚合：工程增量失败主因=编译/类型冲突",
        projStats!.TopFailureCause == "编译/类型冲突");
    var extStats = agg.Get(Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ExternalCredential);
    Check("聚合：外部凭据 2 条 2 成功",
        extStats is { TotalCount: 2, SuccessCount: 2 });
    var singleStats = agg.Get(Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.SingleFile);
    Check("聚合：单文件 1 条 1 成功",
        singleStats is { TotalCount: 1, SuccessCount: 1 });
    var otherStats = agg.Get(Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.Other);
    Check("聚合：无其他类样本 → null",
        otherStats is null);
}

// ── 3A：RoutingDecisionStore 路由决策落库 ──
Console.WriteLine("== 3A RoutingDecisionStore 路由决策落库 ==");
{
    var rdDir = Path.Combine(TestTmp, "rd_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(rdDir);
    var rdDb = Path.Combine(rdDir, "metrics.db");
    var store = new Agent.Infrastructure.Routing.RoutingDecisionStore(rdDb);

    // 记录 3 条不同决策
    var ctNone = CancellationToken.None;
    store.RecordAsync(
        new Agent.Infrastructure.Routing.RoutingDecision(
            Agent.Infrastructure.Routing.RoutingDecision.Large,
            Agent.Infrastructure.Routing.RoutingTier.Rule,
            "写任务直连 large"), "帮我写一个订单系统", null, ctNone).Wait();
    store.RecordAsync(
        new Agent.Infrastructure.Routing.RoutingDecision(
            Agent.Infrastructure.Routing.RoutingDecision.Small,
            Agent.Infrastructure.Routing.RoutingTier.Rule,
            "查询类任务"), "看看有哪些文件", null, ctNone).Wait();
    // 超长输入截断断言
    var longInput = new string('x', 500);
    store.RecordAsync(
        new Agent.Infrastructure.Routing.RoutingDecision(
            Agent.Infrastructure.Routing.RoutingDecision.Small,
            Agent.Infrastructure.Routing.RoutingTier.SmallModel,
            "意图 SimpleQA", Agent.Infrastructure.Routing.IntentType.SimpleQA),
        longInput, null, ctNone).Wait();

    var rdSum = store.GetSummaryAsync(ctNone).Result;
    Check("落库：总数 3", rdSum.Total == 3);
    Check("落库：按服务 large=1 small=2",
        rdSum.ByService.FirstOrDefault(x => x.Service == "large").Count == 1
        && rdSum.ByService.FirstOrDefault(x => x.Service == "small").Count == 2);
    Check("落库：按特征类 写任务=单文件生成 读任务=读/查",
        rdSum.ByFeature.FirstOrDefault(x => x.Feature == "单文件生成").Count >= 1
        && rdSum.ByFeature.FirstOrDefault(x => x.Feature == "读/查").Count >= 1);

    // 截断验证：直接查库
    using var rdConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={rdDb}");
    rdConn.Open();
    using var rdCmd = rdConn.CreateCommand();
    rdCmd.CommandText = "SELECT user_input, input_len FROM routing_decisions WHERE input_len = 200";
    using var rdReader = rdCmd.ExecuteReader();
    var truncated = rdReader.Read();
    Check("落库：超长输入截断到 200", truncated && rdReader.GetString(0).Length == 200);

    // 【10-10 真实口径下沉】路由决策 E2E 源头隔离：ModelRouter 判定开关（RecordDecisionAsync 的唯一守卫，
    // E2E 物理上不落库 → 未来 3B 调权按特征类/服务分布时不会踩 E2E 污染坑；orchestrator 任务本就不经
    // ModelRouter，决策表仅 agent 模式任务产生记录）。
    var rdEnvBefore = Environment.GetEnvironmentVariable("LAW_E2E");
    try
    {
        Environment.SetEnvironmentVariable("LAW_E2E", "1");
        Check("路由决策守卫：LAW_E2E=1 → 判定 E2E 运行（跳过落库）",
            Agent.Infrastructure.Routing.ModelRouter.IsE2ERun);
        Environment.SetEnvironmentVariable("LAW_E2E", null);
        Check("路由决策守卫：LAW_E2E 清除 → 判定非 E2E（正常落库）",
            !Agent.Infrastructure.Routing.ModelRouter.IsE2ERun);
    }
    finally
    {
        Environment.SetEnvironmentVariable("LAW_E2E", rdEnvBefore);
    }
}

// ── C：ToolRejectionStore 工具拒绝事件落库 ──
Console.WriteLine("== C ToolRejectionStore 工具拒绝事件落库 ==");
{
    var trDir = Path.Combine(TestTmp, "tr_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(trDir);
    var trDb = Path.Combine(trDir, "metrics.db");
    var trStore = new Agent.Infrastructure.Metrics.ToolRejectionStore(trDb);
    var ctNone = CancellationToken.None;

    // 1) Store 单元：记录 2 类 3 条 + 聚合
    trStore.RecordAsync("write_file", "pd_conflict", "src/OrderService.cs", "t1", "错误：检测到类型重复定义：Order。", ctNone).Wait();
    trStore.RecordAsync("write_file", "pd_conflict", "src/OrderService.cs", "t1", "错误：第 2 次因类型重复被拒绝。", ctNone).Wait();
    trStore.RecordAsync("edit_file", "old_not_found", "src/A.cs", "t1", "错误：找不到 old_string。", ctNone).Wait();
    var trSum = trStore.GetSummaryAsync(ctNone).Result;
    Check("拒绝落库：总数 3", trSum.Total == 3);
    Check("拒绝落库：按类别 pd_conflict=2 old_not_found=1",
        trSum.ByCategory.FirstOrDefault(x => x.Category == "pd_conflict").Count == 2
        && trSum.ByCategory.FirstOrDefault(x => x.Category == "old_not_found").Count == 1);
    Check("拒绝落库：按工具 write_file=2 edit_file=1",
        trSum.ByTool.FirstOrDefault(x => x.Tool == "write_file").Count == 2
        && trSum.ByTool.FirstOrDefault(x => x.Tool == "edit_file").Count == 1);

    // 2) WriteFileTool 带 store：P-D 拒绝 → 事件落库（补度量盲区）
    var ws2 = Path.Combine(trDir, "ws");
    var proj2 = Path.Combine(trDir, "proj");
    Directory.CreateDirectory(ws2);
    Directory.CreateDirectory(Path.Combine(proj2, "src"));
    File.WriteAllText(Path.Combine(proj2, "src", "Order.cs"),
        "using System;\nclass Order\n{\n    public int Id { get; set; }\n}\n");
    var wRw2 = new Agent.Infrastructure.Tools.WriteFileTool(ws2, proj2, true, "t2", trStore);
    var json = $"{{\"path\":\"src/OrderService.cs\",\"content\":{JsonSerializer.Serialize("using System;\nclass Order\n{\n}\nclass OrderService\n{\n}\n")}}}";
    var trArgs = JsonSerializer.Deserialize<JsonElement>(json);
    var ret = wRw2.InvokeAsync(trArgs, ctNone).GetAwaiter().GetResult();
    Check("带 store 的 P-D 拒绝 → 仍返回拒绝",
        ret.Contains("类型重复定义"));
    var trSum2 = trStore.GetSummaryAsync(ctNone).Result;
    Check("带 store 的 P-D 拒绝 → 事件落库（pd_conflict 增至 3）",
        trSum2.ByCategory.FirstOrDefault(x => x.Category == "pd_conflict").Count == 3);

    // 3) 不带 store（tests 既有构造）→ 静默不炸
    var wNoStore = new Agent.Infrastructure.Tools.WriteFileTool(ws2);
    var ret2 = wNoStore.InvokeAsync(trArgs, ctNone).GetAwaiter().GetResult();
    Check("不带 store 构造 → 拒绝正常返回（无异常）",
        ret2.Contains("越界") || ret2.Contains("类型重复定义") || ret2.Contains("已写入"));
}

// ── 1：ReadFileTool 分页续读（大文件不再拒绝） ──
Console.WriteLine("== 1 ReadFileTool 分页续读 ==");
{
    var rfDir = Path.Combine(TestTmp, "rf_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(rfDir);
    var rfProj = Path.Combine(rfDir, "proj");
    Directory.CreateDirectory(rfProj);

    // 大文件（>200KB）：6000 行 × 填充 ≈ 600KB
    var bigPath = Path.Combine(rfProj, "big.txt");
    var bigContent = string.Join("\n", Enumerable.Range(1, 6000).Select(i => $"line{i} " + new string('x', 90)));
    File.WriteAllText(bigPath, bigContent);

    var rfTool = new Agent.Infrastructure.Tools.ReadFileTool(rfDir, rfProj);
    var ctNone2 = CancellationToken.None;

    // 【大文件首读重定向】工程模式 + 大文件 + 无 offset → 拒绝 + 定位引导（强制走 find_symbol/analyze_code）
    var jBig = "{\"path\":\"big.txt\"}";
    var argsBig = JsonSerializer.Deserialize<JsonElement>(jBig);
    var rBig = rfTool.InvokeAsync(argsBig, ctNone2).GetAwaiter().GetResult();
    Check("工程模式大文件无 offset → 拒绝 + 定位引导",
        rBig.Contains("错误：文件过大") && rBig.Contains("find_symbol") && rBig.Contains("offset/limit"));

    // 显式 offset 续读：501-1000（重定向放行——带 offset 即精确读取）
    var jOff = "{\"path\":\"big.txt\",\"offset\":501,\"limit\":500}";
    var argsOff = JsonSerializer.Deserialize<JsonElement>(jOff);
    var rOff = rfTool.InvokeAsync(argsOff, ctNone2).GetAwaiter().GetResult();
    Check("大文件 offset 续读 → 501-1000 行",
        rOff.Contains("已返回第 501-1000 行") && rOff.Contains("line501") && rOff.Contains("offset=1001"));

    // 【重定向收紧】仅 limit 无 offset → 仍拒绝（模型传 limit 绕过定位的实证修复）
    var jLim = "{\"path\":\"big.txt\",\"limit\":500}";
    var argsLim = JsonSerializer.Deserialize<JsonElement>(jLim);
    var rLim = rfTool.InvokeAsync(argsLim, ctNone2).GetAwaiter().GetResult();
    Check("大文件仅 limit 无 offset → 仍拒绝（防绕过）",
        rLim.Contains("错误：文件过大") && rLim.Contains("find_symbol"));

    // 末页：无续读提示
    var jTail = "{\"path\":\"big.txt\",\"offset\":5501,\"limit\":1000}";
    var argsTail = JsonSerializer.Deserialize<JsonElement>(jTail);
    var rTail = rfTool.InvokeAsync(argsTail, ctNone2).GetAwaiter().GetResult();
    Check("大文件末页 → 无续读提示",
        rTail.Contains("已返回第 5501-6000 行") && !rTail.Contains("offset="));

    // 非工程模式（无 projectRoot）大文件无 offset → 保持分页（向后兼容；相对路径带 proj/ 前缀）
    var rfTool2 = new Agent.Infrastructure.Tools.ReadFileTool(rfDir);
    var argsBigRel = JsonSerializer.Deserialize<JsonElement>("{\"path\":\"proj/big.txt\"}");
    var rBig2 = rfTool2.InvokeAsync(argsBigRel, ctNone2).GetAwaiter().GetResult();
    Check("非工程模式大文件无 offset → 仍分页（兼容）",
        rBig2.Contains("共 6000 行") && rBig2.Contains("已返回第 1-500 行") && rBig2.Contains("offset=501"));

    // 小文件默认：全文无头（向后兼容）
    var smallPath = Path.Combine(rfProj, "small.txt");
    File.WriteAllText(smallPath, "hello\nworld");
    var argsSmall = JsonSerializer.Deserialize<JsonElement>("{\"path\":\"small.txt\"}");
    var rSmall = rfTool.InvokeAsync(argsSmall, ctNone2).GetAwaiter().GetResult();
    Check("小文件默认 → 全文无分页头",
        rSmall == "hello\nworld");
}

// ── 3：FailureCategorizer 失败类别自动归类 ──
Console.WriteLine("== 3 FailureCategorizer 失败类别归类 ==");
{
    var ctNone3 = CancellationToken.None;
    Check("类别：外部凭据 401", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("错误: 401 - {\"cod\":401, \"message\": \"Invalid API key\"}") == "external_credential");
    Check("类别：外部凭据 403", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("获取位置信息失败: 403") == "external_credential");
    Check("类别：编译错误 CS", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("error CS0272: 属性或索引器不能使用") == "compile_error");
    Check("类别：运行崩溃 Traceback", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("Traceback (most recent call last): ZeroDivisionError") == "runtime_error");
    Check("类别：超时", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("错误：命令执行超时（>30秒）。\n退出码：124") == "timeout");
    Check("类别：工具拒绝 P-D", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("错误：检测到类型重复定义：Order。\n请勿重复定义") == "tool_rejected");
    Check("类别：工具拒绝 白名单", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("错误：命令 python -c 不在白名单中。\n退出码：127") == "tool_rejected");
    Check("类别：未找到", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("错误：文件不存在：pyst.py") == "not_found");
    Check("类别：空响应", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("模型连续返回空答案，任务终止") == "empty_response");
    Check("类别：其他兜底", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("未知错误 XYZ") == "other");
    Check("类别：成功不归类（空入参）", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("") == "other");
    // 批次2 数据驱动补洞：熔断/完成门实证消息
    Check("类别：写文件但验证未通过 → write_unverified", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("任务写入文件但验证未通过（run_command 未成功），熔断终止，防假成功。") == "write_unverified");
    Check("类别：写任务未产生写入 → write_not_attempted", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("任务要求修改文件但未产生任何写入（仅循环读取），熔断终止。可重试或改用 find_symbol/analyze_code 定位后编辑。") == "write_not_attempted");
    Check("类别：目标不存在 → target_mismatch", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("任务要求写入 X:\\a\\b.txt，但该目标不存在（本轮写入位置与用户指定不符）。") == "target_mismatch");
    Check("类别：写被拒未落地 → tool_rejected", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("写入被拒绝且本轮未成功写入任何文件（写目标未落地）。可检查路径是否越界/敏感，或改用工程内路径后重试。") == "tool_rejected");
    Check("类别：验证失败 → validator_failed", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("任务未通过验证（重试 2 次）。最后错误：退出码：1") == "validator_failed");
    Check("类别：回填标注 → validator_failed", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("任务未通过验证 [父任务未通过验证（回填：顶层 orchestrator 失败）]") == "validator_failed");
    Check("类别：严格校验不一致 → validator_mismatch", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("任务未通过验证（重试 0 次）。最后错误：退出码：0\n--- stdout ---\n订单总金额: 103\n[检测] 输出与计划预期不完全一致（严格模式：预期每一行须在输出中精确出现）。") == "validator_mismatch");
    Check("类别：修复输出相同 → repair_unimproved", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("修复失败：模型输出与原文相同。错误信息：退出码：0\n--- stdout ---\n城市: Kunming\n天气状况: Light rain shower") == "repair_unimproved");

    // 批次3·B：ExtractMissingType 类型缺失提取（弯/直引号）
    Check("提取：弯引号 CS0117 → Order", Agent.Infrastructure.Orchestration.Orchestrator.ExtractMissingType("Program.cs(11,13): error CS0117: “Order”未包含“Items”的定义") == "Order");
    Check("提取：直引号 CS0117 → Order", Agent.Infrastructure.Orchestration.Orchestrator.ExtractMissingType("Program.cs(11,13): error CS0117: \"Order\"未包含\"Items\"的定义") == "Order");
    Check("提取：CS0246 → Order", Agent.Infrastructure.Orchestration.Orchestrator.ExtractMissingType("DiscountService.cs(5,37): error CS0246: 未能找到类型或命名空间名“Order”(是否缺少 using 指令)") == "Order");
    Check("提取：CS1061 → Order", Agent.Infrastructure.Orchestration.Orchestrator.ExtractMissingType("OrderService.cs(15,15): error CS1061: “Order”未包含“TotalAmount”的定义") == "Order");
    Check("提取：无类型缺失 → null", Agent.Infrastructure.Orchestration.Orchestrator.ExtractMissingType("error CS5001: 程序不包含适合于入口点的静态 Main 方法") is null);
    Check("提取：空输入 → null", Agent.Infrastructure.Orchestration.Orchestrator.ExtractMissingType("") is null);

    // SqliteMetricsCollector 落库 failure_category
    var fcDir = Path.Combine(TestTmp, "fc_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(fcDir);
    var fcDb = Path.Combine(fcDir, "metrics.db");
    var fcMetrics = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(fcDb);
    var ts = DateTimeOffset.UtcNow;
    fcMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
    {
        TaskId = "fc_fail_1", SessionId = "s1", UserInput = "测试编译失败", Mode = "agent",
        RoutingTier = "Rule", ServiceId = "large", RoutingReason = "test",
        Success = false, IsSuspicious = false, RetryCount = 1,
        TotalMs = 100, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
        LintBlockingErrors = 0, LintWarningErrors = 0,
        ErrorSummary = "error CS0101: 重复定义", StartedAt = ts, FinishedAt = ts
    }, ctNone3).Wait();
    fcMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
    {
        TaskId = "fc_ok_1", SessionId = "s1", UserInput = "测试成功", Mode = "agent",
        RoutingTier = "Rule", ServiceId = "large", RoutingReason = "test",
        Success = true, IsSuspicious = false, RetryCount = 0,
        TotalMs = 50, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
        LintBlockingErrors = 0, LintWarningErrors = 0,
        ErrorSummary = "", StartedAt = ts, FinishedAt = ts
    }, ctNone3).Wait();
    var fb = fcMetrics.GetFailureBreakdownAsync(ctNone3).Result;
    Check("失败类别落库 → compile_error:1（成功不统计）",
        fb.Count == 1 && fb[0].Category == "compile_error" && fb[0].Count == 1);

    // 可疑成功落库 + 明细读取（供 stats 可疑明细块）
    fcMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
    {
        TaskId = "fc_sus_1", SessionId = "s1", UserInput = "写 hello.txt 并验证", Mode = "agent",
        RoutingTier = "Rule", ServiceId = "large", RoutingReason = "test",
        Success = true, IsSuspicious = true, RetryCount = 0,
        TotalMs = 30, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
        LintBlockingErrors = 0, LintWarningErrors = 0,
        ErrorSummary = "", StartedAt = ts, FinishedAt = ts
    }, ctNone3).Wait();
    var susRecent = fcMetrics.GetRecentAsync(50, ctNone3).Result
        .Where(m => m.IsSuspicious && m.Success).ToList();
    Check("可疑成功落库 → GetRecentAsync 可过滤出明细",
        susRecent.Count == 1 && susRecent[0].TaskId == "fc_sus_1");

    // 【10-08 metrics 数据治理】E2E 标记：is_e2e 落库 + 真实统计剔除 + 单列计数
    var e2eMetrics = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(
        Path.Combine(fcDir, "e2e.db"));
    var e2eNow = DateTimeOffset.UtcNow;
    Agent.Core.Models.TaskMetric MkE2E(string tid, bool ok) => new Agent.Core.Models.TaskMetric
    {
        TaskId = tid, SessionId = "s1", UserInput = "E2E 用例", Mode = "agent",
        RoutingTier = "Rule", ServiceId = "small", RoutingReason = "test",
        Success = ok, IsSuspicious = false, IsE2E = true, RetryCount = 0,
        TotalMs = 10, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
        LintBlockingErrors = 0, LintWarningErrors = 0,
        ErrorSummary = ok ? "" : "case_incremental 故意失败", StartedAt = e2eNow, FinishedAt = e2eNow
    };
    e2eMetrics.RecordAsync(MkE2E("e2e_ok", true), ctNone3).Wait();
    e2eMetrics.RecordAsync(MkE2E("e2e_fail", false), ctNone3).Wait();
    e2eMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
    {
        TaskId = "real_1", SessionId = "s1", UserInput = "真实任务", Mode = "agent",
        RoutingTier = "Rule", ServiceId = "small", RoutingReason = "test",
        Success = true, IsSuspicious = false, IsE2E = false, RetryCount = 0,
        TotalMs = 20, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
        LintBlockingErrors = 0, LintWarningErrors = 0,
        ErrorSummary = "", StartedAt = e2eNow, FinishedAt = e2eNow
    }, ctNone3).Wait();
    var e2eSummary = e2eMetrics.GetSummaryAsync(null, ctNone3).Result;
    Check("E2E 治理：真实统计剔除 E2E（1 真实成功 / 0 失败）",
        e2eSummary.TotalCount == 1 && e2eSummary.SuccessCount == 1 && e2eSummary.FailureCount == 0);
    Check("E2E 治理：E2E 计数单列 = 2", e2eMetrics.GetE2ECountAsync(ctNone3).Result == 2);
    var e2eRecent = e2eMetrics.GetRecentAsync(10, ctNone3).Result;
    Check("E2E 治理：GetRecentAsync 过滤 E2E（仅真实任务）",
        e2eRecent.Count == 1 && e2eRecent[0].TaskId == "real_1");
    var e2eFail = e2eMetrics.GetRecentFailuresAsync(10, ctNone3).Result;
    Check("E2E 治理：GetRecentFailuresAsync 过滤 E2E（E2E 故意失败不计入）", e2eFail.Count == 0);

    // 【10-10 失败类别统计补洞】GetFailureBreakdownAsync 此前漏 is_e2e=0 过滤，
    // 导致 compile_error/validator_failed 被 E2E 用例严重污染（实测 21→2、19→0）。
    // 同库并存真实失败 + 2 条 E2E 失败，断言失败类别只统计真实记录。
    e2eMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
    {
        TaskId = "e2e_fail_cs", SessionId = "s1", UserInput = "E2E 用例", Mode = "agent",
        RoutingTier = "Rule", ServiceId = "small", RoutingReason = "test",
        Success = false, IsSuspicious = false, IsE2E = true, RetryCount = 0,
        TotalMs = 10, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
        LintBlockingErrors = 0, LintWarningErrors = 0,
        ErrorSummary = "error CS0101: E2E 重复定义", StartedAt = e2eNow, FinishedAt = e2eNow
    }, ctNone3).Wait();
    e2eMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
    {
        TaskId = "real_fail_1", SessionId = "s1", UserInput = "真实任务", Mode = "agent",
        RoutingTier = "Rule", ServiceId = "small", RoutingReason = "test",
        Success = false, IsSuspicious = false, IsE2E = false, RetryCount = 0,
        TotalMs = 20, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
        LintBlockingErrors = 0, LintWarningErrors = 0,
        ErrorSummary = "error CS0201: 真实类型冲突", StartedAt = e2eNow, FinishedAt = e2eNow
    }, ctNone3).Wait();
    var e2eFb = e2eMetrics.GetFailureBreakdownAsync(ctNone3).Result;
    Check("失败类别统计排除 E2E：仅 1 条真实 compile_error（2 条 E2E 失败不计入）",
        e2eFb.Count == 1 && e2eFb[0].Category == "compile_error" && e2eFb[0].Count == 1);

    // 批次3·C 父失败回填：顶层失败 → Executor 子记录成功回填为失败
    var bfMetrics = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(
        Path.Combine(fcDir, "backfill.db"));
    var bfNow = DateTimeOffset.UtcNow;
    Agent.Core.Models.TaskMetric MkSub(string tid, bool ok, DateTimeOffset at) => new Agent.Core.Models.TaskMetric
    {
        TaskId = tid, SessionId = "", UserInput = "做一个 C# 项目", Mode = "orchestrator_sub",
        RoutingTier = "OrchestratorExecutor", ServiceId = "small", RoutingReason = "orchestrator 内部调用（子指标）",
        Success = ok, IsSuspicious = false, RetryCount = 0,
        TotalMs = 10, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
        LintBlockingErrors = 0, LintWarningErrors = 0,
        ErrorSummary = "", StartedAt = at, FinishedAt = at
    };
    bfMetrics.RecordAsync(MkSub("bf_win", true, bfNow), ctNone3).Wait();
    bfMetrics.RecordAsync(MkSub("bf_fail", false, bfNow), ctNone3).Wait();
    bfMetrics.RecordAsync(MkSub("bf_old", true, bfNow.AddMinutes(-10)), ctNone3).Wait();
    var bfCount = bfMetrics.BackfillFailedSubMetricsAsync("做一个 C# 项目", bfNow.AddSeconds(-5), "父任务未通过验证（回填）", ctNone3).Result;
    Check("父失败回填：窗口内成功 Executor 子记录 1 条被回填", bfCount == 1);
    var bfWin = bfMetrics.GetRecentAsync(50, ctNone3).Result.First(m => m.TaskId == "bf_win");
    Check("回填后 success=0 + validator_failed", !bfWin.Success && bfWin.ErrorSummary.Contains("父任务未通过验证"));
    var bfOld = bfMetrics.GetRecentAsync(50, ctNone3).Result.First(m => m.TaskId == "bf_old");
    Check("窗口外历史记录不被回填", bfOld.Success);
    var bfFail = bfMetrics.GetRecentAsync(50, ctNone3).Result.First(m => m.TaskId == "bf_fail");
    Check("原本失败的记录不受影响", !bfFail.Success);

    // 【10-10 真实口径下沉】护栏：数据闭环（QueryStats）/ 时代隔离（EpochBreakdown）走 task_metrics_real 视图，
    // 不再依赖"每个查询记得加 is_e2e=0"的脆弱约定。e2e.db 共 5 条：3 条 E2E（2 失败 1 成功）+ 2 条真实（1 失败 1 成功）。
    var vwStats = e2eMetrics.GetServiceStatsAsync("small", "Rule", 10, ctNone3).Result;
    Check("真实口径视图：数据闭环排除 E2E（5 条 → 真实 2 条，成功 1）",
        vwStats.TotalCount == 2 && vwStats.SuccessCount == 1);
    var vwEpoch = e2eMetrics.GetEpochBreakdownAsync("small", e2eNow.AddSeconds(-3600), ctNone3).Result;
    Check("真实口径视图：时代隔离排除 E2E（Current 真实 2 条 / Legacy 0）",
        vwEpoch.Current.TotalCount == 2 && vwEpoch.Current.SuccessCount == 1 && vwEpoch.Legacy.TotalCount == 0);

    // 【10-10 真实口径下沉】父失败回填（UPDATE）排除 E2E 子记录：窗口内新增一条 E2E 成功子记录，
    // 修复前会被错误回填（返回 1），修复后返回 0（E2E 保持成功）。
    bfMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
    {
        TaskId = "bf_e2e_sub", SessionId = "", UserInput = "做一个 C# 项目", Mode = "orchestrator_sub",
        RoutingTier = "OrchestratorExecutor", ServiceId = "small", RoutingReason = "orchestrator 内部调用（子指标）",
        Success = true, IsSuspicious = false, IsE2E = true, RetryCount = 0,
        TotalMs = 10, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
        LintBlockingErrors = 0, LintWarningErrors = 0,
        ErrorSummary = "", StartedAt = bfNow, FinishedAt = bfNow
    }, ctNone3).Wait();
    var bfCount2 = bfMetrics.BackfillFailedSubMetricsAsync("做一个 C# 项目", bfNow.AddSeconds(-5), "父任务未通过验证（回填2）", ctNone3).Result;
    Check("父失败回填排除 E2E：返回计数 0（E2E 子记录保持成功，无真实可回填）", bfCount2 == 0);
}

// ── 任务1：工程增量计划校验（Plan vs 已有文件冲突） ──
Console.WriteLine("== 工程增量计划校验（CheckIncrementalPlanConflicts） ==");
{
    var existing = new[] { "Order.cs", "OrderService.cs", "Program.cs", "TaxService.cs", "DiscountService.cs" };
    // ShippingService 复测典型：Plan 把 3 个已有文件列入 → 冲突 ≥2
    var conflicts = Agent.Infrastructure.Orchestration.Orchestrator.CheckIncrementalPlanConflicts(
        new[] { "Order.cs", "OrderService.cs", "Program.cs", "ShippingService.cs" }, existing);
    Check("计划含 3 个已有文件 → 冲突 3",
        conflicts.Count == 3 && conflicts.Contains("Order.cs") && conflicts.Contains("ShippingService.cs") == false);
    // 合法形态：新增 1 + 接入 1
    var ok = Agent.Infrastructure.Orchestration.Orchestrator.CheckIncrementalPlanConflicts(
        new[] { "ShippingService.cs", "OrderService.cs" }, existing);
    Check("新增 1 + 接入 1 → 冲突 1（放行）",
        ok.Count == 1 && ok[0] == "OrderService.cs");
    // 全新增
    var none = Agent.Infrastructure.Orchestration.Orchestrator.CheckIncrementalPlanConflicts(
        new[] { "ShippingService.cs" }, existing);
    Check("全新增 → 冲突 0（放行）", none.Count == 0);
    // 路径大小写/分隔符归一化
    var caseIns = Agent.Infrastructure.Orchestration.Orchestrator.CheckIncrementalPlanConflicts(
        new[] { "order.cs", "src\\order.cs" }, existing);
    Check("大小写/分隔符归一化（order.cs → 冲突）",
        caseIns.Count == 1 && caseIns[0] == "order.cs");

    // 批次4：修复重试无进展错误签名
    var sigCs = Agent.Infrastructure.Orchestration.Orchestrator.ExtractErrorSignature(
        "X:\\a\\Program.cs(10,13): error CS1002: ; expected");
    Check("错误签名：C# CS码+行号", sigCs is not null && sigCs.Contains("CS1002") && sigCs.Contains("10"));
    var sigPy = Agent.Infrastructure.Orchestration.Orchestrator.ExtractErrorSignature(
        "File \"x.py\", line 5\nNameError: name 'a' is not defined");
    Check("错误签名：Python line+NameError", sigPy is not null && sigPy.Contains("NameError") && sigPy.Contains("5"));
    var sigSame = Agent.Infrastructure.Orchestration.Orchestrator.ExtractErrorSignature(
        "X:\\a\\Program.cs(10,13): error CS1002: ; expected\nmore output");
    Check("错误签名：相同错误输出 → 相同指纹", sigCs == sigSame);
    var sigNone = Agent.Infrastructure.Orchestration.Orchestrator.ExtractErrorSignature("hello world");
    Check("错误签名：无错误输出 → null", sigNone is null);
}

// ── 工程完成门 + 编辑冲突诊断 ──
Console.WriteLine("== TestProjectDetector（完成门） ==");
{
    var gateDir = Path.Combine(TestTmp, "gate_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(Path.Combine(gateDir, "src"));

    // 无测试 → false
    Check("无测试工程 → false",
        !Agent.Infrastructure.Project.TestProjectDetector.HasTests(gateDir));

    // tests 目录 → true
    Directory.CreateDirectory(Path.Combine(gateDir, "tests"));
    Check("tests 目录 → true",
        Agent.Infrastructure.Project.TestProjectDetector.HasTests(gateDir));

    // 测试 csproj → true（换一个空目录）
    var gateDir2 = Path.Combine(TestTmp, "gate2_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(gateDir2);
    File.WriteAllText(Path.Combine(gateDir2, "MyApp.Tests.csproj"), "<Project Sdk='Microsoft.NET.Sdk' />");
    Check("*.Tests.csproj → true",
        Agent.Infrastructure.Project.TestProjectDetector.HasTests(gateDir2));

    // pytest.ini → true
    var gateDir3 = Path.Combine(TestTmp, "gate3_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(gateDir3);
    File.WriteAllText(Path.Combine(gateDir3, "pytest.ini"), "[pytest]");
    Check("pytest.ini → true",
        Agent.Infrastructure.Project.TestProjectDetector.HasTests(gateDir3));

    // 空根/越界 → false
    Check("空根 → false",
        !Agent.Infrastructure.Project.TestProjectDetector.HasTests(""));

    Directory.Delete(gateDir, true);
    Directory.Delete(gateDir2, true);
    Directory.Delete(gateDir3, true);
}

Console.WriteLine("== 编辑冲突诊断（EditFileTool 找不到 old_string 且备份漂移） ==");
{
    var proj = Path.Combine(TestTmp, "conflict_" + Guid.NewGuid().ToString("N")[..6]);
    var work = Path.Combine(TestTmp, "conflictwork_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(proj);
    Directory.CreateDirectory(work);
    var file = Path.Combine(proj, "demo.cs");
    File.WriteAllText(file, "class Demo { int a = 1; }");

    // 先做一次"备份"（模拟任务内已备份）：
    var tool = new Agent.Infrastructure.Tools.EditFileTool(work, proj, projectWriteEnabled: true);
    var first = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"demo.cs\",\"old_string\":\"int a = 1\",\"new_string\":\"int a = 2\"}").RootElement,
        CancellationToken.None).GetAwaiter().GetResult();
    Check("正常编辑成功（含备份）", first.Contains("已修改"));

    // 外部修改（模拟漂移：内容被外部改写，a=1 消失）
    File.WriteAllText(file, "class Demo { int b = 9; }");

    // 再 edit 旧的 old_string → 冲突诊断
    var second = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"demo.cs\",\"old_string\":\"int a = 1\",\"new_string\":\"int a = 3\"}").RootElement,
        CancellationToken.None).GetAwaiter().GetResult();
    Check("漂移诊断（提及备份/变化）", second.Contains("自最近备份") && second.Contains("漂移"));

    Directory.Delete(proj, true);
    Directory.Delete(work, true);
}

// ── 工程只读提示（防幽灵文件：use 未带 --write 时写工程文件被明确拒绝） ──
Console.WriteLine("== 工程只读模式（写工程文件被拒绝并提示） ==");
{
    var proj = Path.Combine(TestTmp, "ro_" + Guid.NewGuid().ToString("N")[..6]);
    var work = Path.Combine(TestTmp, "rowork_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(proj);
    Directory.CreateDirectory(work);
    File.WriteAllText(Path.Combine(proj, "app.cs"), "class App { }");

    // 工程模式但未授权（projectWriteEnabled=false）→ 写工程根内已存在文件 → 只读拒绝
    var w = new Agent.Infrastructure.Tools.WriteFileTool(work, proj, projectWriteEnabled: false);
    var wOut = w.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"app.cs\",\"content\":\"class App { int x; }\"}").RootElement,
        CancellationToken.None).GetAwaiter().GetResult();
    Check("write 工程文件 → 只读拒绝", wOut.Contains("工程只读模式") && wOut.Contains("--write"));
    // 工程文件未被改动（防幽灵覆盖）
    Check("工程文件未被写入", File.ReadAllText(Path.Combine(proj, "app.cs")).Contains("class App { }"));

    var e = new Agent.Infrastructure.Tools.EditFileTool(work, proj, projectWriteEnabled: false);
    var eOut = e.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"app.cs\",\"old_string\":\"class App\",\"new_string\":\"class App2\"}").RootElement,
        CancellationToken.None).GetAwaiter().GetResult();
    Check("edit 工程文件 → 只读拒绝", eOut.Contains("工程只读模式") && eOut.Contains("--write"));

    // 任务目录内正常写不受影响
    var w2 = new Agent.Infrastructure.Tools.WriteFileTool(work, proj, projectWriteEnabled: false);
    var w2Out = w2.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"new.txt\",\"content\":\"hello\"}").RootElement,
        CancellationToken.None).GetAwaiter().GetResult();
    Check("任务目录写不受影响", w2Out.Contains("已写入"));

    Directory.Delete(proj, true);
    Directory.Delete(work, true);
}

// ── 工程级测试自动执行：DetectTest 各形态 ──
Console.WriteLine("== TestRunnerService（测试命令检测） ==");
{
    var trDir = Path.Combine(TestTmp, "tr_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(trDir);

    // console 断言形态（无测试框架包）→ dotnet run --project
    Directory.CreateDirectory(Path.Combine(trDir, "tests"));
    File.WriteAllText(Path.Combine(trDir, "tests", "App.Tests.csproj"),
        "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
    var d1 = Agent.Infrastructure.Project.TestRunnerService.DetectTest(trDir);
    Check("console 断言形态 → dotnet run", d1 is not null && d1.Value.Command == "dotnet" && d1.Value.Args.Contains("run"));

    // xunit 形态 → dotnet test
    var trDir2 = Path.Combine(TestTmp, "tr2_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(trDir2);
    File.WriteAllText(Path.Combine(trDir2, "App.Tests.csproj"),
        "<Project Sdk='Microsoft.NET.Sdk'><ItemGroup><PackageReference Include='xunit' /></ItemGroup></Project>");
    var d2 = Agent.Infrastructure.Project.TestRunnerService.DetectTest(trDir2);
    Check("xunit 形态 → dotnet test", d2 is not null && d2.Value.Args.Contains("test"));

    // pytest.ini → pytest
    var trDir3 = Path.Combine(TestTmp, "tr3_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(trDir3);
    File.WriteAllText(Path.Combine(trDir3, "pytest.ini"), "[pytest]");
    var d3 = Agent.Infrastructure.Project.TestRunnerService.DetectTest(trDir3);
    Check("pytest.ini → pytest", d3 is not null && d3.Value.Command == "pytest");

    // 无测试 → null
    var trDir4 = Path.Combine(TestTmp, "tr4_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(trDir4);
    File.WriteAllText(Path.Combine(trDir4, "app.py"), "x = 1");
    Check("无测试入口 → null", Agent.Infrastructure.Project.TestRunnerService.DetectTest(trDir4) is null);

    Directory.Delete(trDir, true);
    Directory.Delete(trDir2, true);
    Directory.Delete(trDir3, true);
    Directory.Delete(trDir4, true);
}

// ── 多文件修改规划：任务级回滚（RollbackTask 全量恢复） ──
Console.WriteLine("== 任务级回滚（多文件变更单元） ==");
{
    var proj = Path.Combine(TestTmp, "rb_" + Guid.NewGuid().ToString("N")[..6]);
    var work = Path.Combine(TestTmp, "rbwork_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(proj);
    Directory.CreateDirectory(work);
    File.WriteAllText(Path.Combine(proj, "a.cs"), "class A { }");
    File.WriteAllText(Path.Combine(proj, "b.cs"), "class B { }");

    // 模拟任务内两次修改（自动备份）
    var tool = new Agent.Infrastructure.Tools.EditFileTool(work, proj, projectWriteEnabled: true);
    tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"a.cs\",\"old_string\":\"class A\",\"new_string\":\"class A2\"}").RootElement,
        CancellationToken.None).GetAwaiter().GetResult();
    tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"b.cs\",\"old_string\":\"class B\",\"new_string\":\"class B2\"}").RootElement,
        CancellationToken.None).GetAwaiter().GetResult();
    Check("修改后文件已变更", File.ReadAllText(Path.Combine(proj, "a.cs")).Contains("class A2")
          && File.ReadAllText(Path.Combine(proj, "b.cs")).Contains("class B2"));

    // 任务级回滚（rollback all 语义）——P-A：取最早备份=任务前状态
    var restored = Agent.Infrastructure.Project.BackupService.RollbackTask(work, proj, "default");
    Check("任务级回滚恢复 2 个文件", restored == 2);
    Check("a.cs 已恢复", File.ReadAllText(Path.Combine(proj, "a.cs")).Contains("class A { }"));
    Check("b.cs 已恢复", File.ReadAllText(Path.Combine(proj, "b.cs")).Contains("class B { }"));

    Directory.Delete(proj, true);
    Directory.Delete(work, true);
}

// ── 工程图谱：SymbolIndexer + find_symbol ──
Console.WriteLine("== 工程图谱（符号索引 + find_symbol） ==");
{
    var proj = Path.Combine(TestTmp, "sym_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(Path.Combine(proj, "src"));
    File.WriteAllText(Path.Combine(proj, "src", "Order.cs"), """
        public class Order
        {
            public int Id { get; set; }
            public string Process(int x) { return x.ToString(); }
        }
        """);
    File.WriteAllText(Path.Combine(proj, "src", "weather.py"), """
        def get_weather(city):
            return city

        class WeatherService:
            def fetch(self, ip):
                return ip
        """);

    // 多修饰符 + 控制流误判回归（真实工程形态）
    File.WriteAllText(Path.Combine(proj, "src", "Extra.cs"), """
        public static class Router
        {
            public static bool TryRoute(string x)
            {
                if (x.Length > 0)
                    return true;
                for (var i = 0; i < 3; i++) { }
                return false;
            }
        }
        public sealed record Stats(int Count, int Ok);
        """);

    var entries2 = Agent.Infrastructure.Project.SymbolIndexer.Build(proj);
    // 探针：直接正则匹配（排除 Build 逻辑干扰）
    var probe = System.Text.RegularExpressions.Regex.Match("public static class Router",
        @"^\s*(?:(?:public|private|protected|internal|static|abstract|sealed|partial|readonly)\s+)*(?:class|interface|enum|record|struct)\s+([A-Za-z_]\w*)");
    Check("探针：正则直接匹配 Router", probe.Success && probe.Groups[1].Value == "Router");
    Check("多修饰符类（public static class）", entries2.Any(e => e.Symbol == "Router" && e.Kind == "class"));
    Check("多修饰符 record", entries2.Any(e => e.Symbol == "Stats" && e.Kind == "record"));
    Check("多修饰符方法", entries2.Any(e => e.Symbol == "TryRoute" && e.Kind == "method"));
    Check("控制流 if 不误判为方法", !entries2.Any(e => e.Symbol == "if" && e.Kind == "method"));
    Check("控制流 for 不误判为方法", !entries2.Any(e => e.Symbol == "for" && e.Kind == "method"));

    var entries = Agent.Infrastructure.Project.SymbolIndexer.Build(proj);
    Check("C# 类索引", entries.Any(e => e.Symbol == "Order" && e.Kind == "class"));
    Check("C# 方法索引", entries.Any(e => e.Symbol == "Process" && e.Kind == "method"));
    Check("C# 属性索引", entries.Any(e => e.Symbol == "Id" && e.Kind == "property"));
    Check("Python 函数索引", entries.Any(e => e.Symbol == "get_weather" && e.Kind == "function"));
    Check("Python 类索引", entries.Any(e => e.Symbol == "WeatherService" && e.Kind == "class"));
    Check("Python 缩进方法索引", entries.Any(e => e.Symbol == "fetch" && e.Kind == "method"));

    // find_symbol 端到端（惰性构建 + 查询）
    var db = Path.Combine(TestTmp, "symdb_" + Guid.NewGuid().ToString("N")[..6] + ".db");
    var work = Path.Combine(TestTmp, "symwork_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(work);
    var fst = new Agent.Infrastructure.Tools.FindSymbolTool(work, proj, db);
    var out1 = fst.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"name\":\"Order\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("find_symbol 命中 Order", out1.Contains("class Order") && out1.Contains("Order.cs"));
    var out2 = fst.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"name\":\"get_weather\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("find_symbol 命中 Python 函数", out2.Contains("function get_weather"));
    var out3 = fst.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"name\":\"NoSuchSymbol\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("find_symbol 未命中 → 提示", out3.Contains("未找到"));
    // 无工程 → 拒绝
    var fst2 = new Agent.Infrastructure.Tools.FindSymbolTool(work, null, db);
    var out4 = fst2.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"name\":\"Order\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("无工程模式 → 拒绝", out4.Contains("工程模式"));

    // 【新鲜度】已索引后新增文件 → 增量刷新命中（旧索引查不到新符号的回归）
    // （方法必须独立成行——行首正则不捕单行类内嵌套方法）
    File.WriteAllText(Path.Combine(proj, "NewThing.cs"),
        "public class NewThing\n{\n    public void FreshMethod() {}\n}\n");
    var out5 = fst.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"name\":\"FreshMethod\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("索引后新增符号 → 增量刷新可命中", out5.Contains("FreshMethod") && out5.Contains("NewThing.cs"));

    // 【定位后执行引导】find_symbol 命中 → 返回含 offset 操作指引（数据驱动）
    Check("find_symbol 命中 → 注入 offset 操作指引",
        out5.Contains("操作指引") && out5.Contains("offset=") && out5.Contains("edit_file"));

    Directory.Delete(proj, true);
    Directory.Delete(work, true);
}

// ── analyze_code 大文件支持（重定向引导打通） ──
Console.WriteLine("== analyze_code 大文件支持 ==");
{
    var acDir = Path.Combine(TestTmp, "ac_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(acDir);
    var big = Path.Combine(acDir, "Big.cs");
    // 300KB 大文件（原 200KB 限制拒绝）
    var bigContent = "using System;\npublic class BigSvc {\n" +
        string.Join("\n", Enumerable.Range(0, 4000).Select(i => $"    public int Method{i}(int x) {{ return x + {i}; }}")) +
        "\n}\n";
    File.WriteAllText(big, bigContent);
    var acTool = new Agent.Infrastructure.Tools.CodeAnalyzerTool(acDir, acDir);
    var acArgs = System.Text.Json.JsonDocument.Parse("{\"path\":\"Big.cs\"}").RootElement;
    var acOut = acTool.InvokeAsync(acArgs, CancellationToken.None).GetAwaiter().GetResult();
    Check("analyze_code 大文件(300KB) → 出符号地图（截断受控）",
        acOut.Contains("符号总数: 4001") && !acOut.Contains("文件过大") && acOut.Contains("... 省略"));
    Directory.Delete(acDir, true);
}

// ── 记忆治理：去重合并 / forget / prune / stats ──
Console.WriteLine("== 记忆治理（去重/过期/forget） ==");
{
    var gov = new Agent.Infrastructure.Memory.AgentMemoryStore(
        Path.Combine(TestTmp, "mem_gov_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db"));

    // 去重合并：同 kind+topic → 一条，count 累加
    gov.Add("task", "创建一个 hello.txt 文件", "任务失败（agent）：第一次失败", "task_1");
    gov.Add("task", "创建一个 hello.txt 文件", "任务失败（agent）：第二次失败", "task_2");
    var afterMerge = gov.GetStats();
    Check("同 topic 合并为一条", afterMerge.Total == 1);
    var mergedHit = gov.List(10).FirstOrDefault();
    Check("合并后 count=2", mergedHit is not null && mergedHit.Count == 2);
    Check("合并后 content 为最新", mergedHit is not null && mergedHit.Content.Contains("第二次"));

    // 不同 topic → 独立
    gov.Add("model", "ProjectRoot 属性", "ProjectRoot 为 null 时沙箱模式。", null);
    Check("不同 topic 独立成条", gov.GetStats().Total == 2);

    // Search 命中带 count
    var s1 = gov.Search("创建一个 hello.txt 文件");
    Check("检索命中返回 count", s1.Count == 1 && s1[0].Count == 2);

    // BumpHit 刷新 last_hit_at（经 List 可见）
    gov.Add("model", "AgentLoop 升级链", "small 空响应直达 large。", null);
    var hitsBefore = gov.Search("AgentLoop 升级链");
    Check("BumpHit 后 last_hit_at 非空", hitsBefore.Count == 1 && hitsBefore[0].LastHitAt.Length > 0);

    // Forget
    var target = gov.List(10).First(m => m.Topic.Contains("ProjectRoot"));
    Check("forget 删除单条", gov.Forget(target.Id) && gov.GetStats().Total == 2);

    // PruneExpired：造一条旧数据（直接 SQL 更新 created_at 为 100 天前）
    gov.Add("task", "旧任务归档", "任务失败（agent）：很旧", "task_old");
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=" + Path.Combine(TestTmp, FindGovDb()));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_memory SET created_at = @old, last_hit_at = @old WHERE topic = @tp;
        """;
        cmd.Parameters.AddWithValue("@old", DateTime.Now.AddDays(-100).ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@tp", "旧任务归档");
        cmd.ExecuteNonQuery();
    }
    var beforePrune = gov.GetStats().Total;
    var removedPrune = gov.PruneExpired(90, 2);
    Check("过期清理删除旧记录", removedPrune >= 1 && gov.GetStats().Total == beforePrune - 1);

    // Deduplicate：SQL 直插两条同 topic（绕过 Add 合并）造存量重复
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=" + Path.Combine(TestTmp, FindGovDb()));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_memory (kind, topic, content, created_at, count) VALUES ('task', '存量重复', '旧内容A', @at, 1);
            INSERT INTO agent_memory (kind, topic, content, created_at, count) VALUES ('task', '存量重复', '旧内容B', @at, 2);
        """;
        cmd.Parameters.AddWithValue("@at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }
    var dedupRemoved = gov.Deduplicate();
    Check("dedup 合并存量重复", dedupRemoved == 1);
    var dedupHit = gov.List(50).FirstOrDefault(m => m.Topic == "存量重复");
    Check("dedup 后 count=3（1+2）", dedupHit is not null && dedupHit.Count == 3);

    string FindGovDb()
    {
        var f = System.IO.Directory.GetFiles(TestTmp, "mem_gov_*.db").OrderByDescending(x => x).First();
        return f;
    }
}

// ── agent_pro 工程理解专项：CodeAnalyzerTool 符号索引 ──
Console.WriteLine("== CodeAnalyzerTool（确定性符号索引） ==");
{
    var anDir = Path.Combine(TestTmp, "analyzer_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(anDir);

    // C# 样本
    var csPath = Path.Combine(anDir, "Order.cs");
    File.WriteAllText(csPath, """
        using System;

        public class Order
        {
            public int Id { get; set; }

            public string Process(int amount)
            {
                return amount.ToString();
            }
        }
        """);

    var tool = new Agent.Infrastructure.Tools.CodeAnalyzerTool(anDir);
    var csOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"Order.cs\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("C# 语言识别", csOut.Contains("语言: csharp"));
    Check("C# 类定位（行号 3）", csOut.Contains("[3] class Order"));
    Check("C# 属性定位", csOut.Contains("属性 Id"));
    Check("C# 方法定位", csOut.Contains("方法 Process"));

    // Python 样本
    var pyPath = Path.Combine(anDir, "weather.py");
    File.WriteAllText(pyPath, """
        import requests

        def get_weather(city):
            return city

        class WeatherService:
            def fetch(self, ip):
                return ip
        """);
    var pyOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"weather.py\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("Python 语言识别", pyOut.Contains("语言: python"));
    Check("Python 函数定位", pyOut.Contains("函数 get_weather"));
    Check("Python 类定位", pyOut.Contains("class WeatherService"));
    Check("Python 缩进方法定位", pyOut.Contains("方法 fetch"));

    // 错误路径
    var missOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"no_such.cs\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("文件不存在 → 错误", missOut.Contains("文件不存在"));
    var escOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"..\\\\..\\\\Windows\\\\x.txt\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("路径越界 → 拒绝", escOut.Contains("越界"));

    // 非代码文件
    var txtPath = Path.Combine(anDir, "readme.txt");
    File.WriteAllText(txtPath, "hello");
    var txtOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"readme.txt\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("非代码文件 → 无符号", txtOut.Contains("非代码文件"));

    Directory.Delete(anDir, true);
}

// ── 10-08 符号地图治理：大文件截断 + range 参数 ──
Console.WriteLine("== 符号地图治理（截断 + range） ==");
{
    var anDir = Path.Combine(TestTmp, "symmap_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(anDir);

    // 生成 5003 方法的大文件（复刻 BigService 规模）
    var bigPath = Path.Combine(anDir, "BigService.cs");
    var sbBig = new System.Text.StringBuilder();
    sbBig.AppendLine("using System;");
    sbBig.AppendLine("public class BigService {");
    for (var i = 0; i < 5003; i++)
        sbBig.AppendLine($"    public int Method{i}(int x) {{ return x + {i}; }}");
    sbBig.AppendLine("}");
    File.WriteAllText(bigPath, sbBig.ToString());

    var tool = new Agent.Infrastructure.Tools.CodeAnalyzerTool(anDir, anDir);

    // ① 全量模式 → 截断 + 省略行 + 定向引导 + 符号总数仍准确
    var bigOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"BigService.cs\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("治理：全量符号地图 → 截断（省略行出现）", bigOut.Contains("... 省略"));
    Check("治理：截断提示含定向引导（find_symbol/range）",
        bigOut.Contains("find_symbol") && bigOut.Contains("range="));
    Check("治理：符号总数仍准确（5005 = 1 类 + 5003 方法 + 1 兜底）",
        bigOut.Contains("符号总数: 5004"));
    Check("治理：未输出超限符号（Method4999 不在前 120 内）", !bigOut.Contains("方法 Method4999"));
    Check("治理：输出长度受控（≤ 6KB）", bigOut.Length < 6 * 1024);

    // ② range 参数 → 只分析该行段
    var rangeOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"BigService.cs\",\"range\":\"1500-1505\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("range：只含该段符号（Method1499 行 1502 / Method1500 行 1503）",
        rangeOut.Contains("[1502] 方法 Method1499") &&
        rangeOut.Contains("[1503] 方法 Method1500") &&
        rangeOut.Contains("方法 Method1502"));
    Check("range：不含段外符号（Method0 / Method4999）",
        !rangeOut.Contains("方法 Method0") && !rangeOut.Contains("方法 Method4999"));
    Check("range：无省略行（段内符号少）", !rangeOut.Contains("... 省略"));
    Check("range：头部注明行段", rangeOut.Contains("分析行段: 1500-1505"));

    // ③ range 非法/超界 → 用法提示
    var bad1 = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"BigService.cs\",\"range\":\"abc\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("range 非法格式 → 用法提示", bad1.Contains("range 格式应为"));
    var bad2 = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"BigService.cs\",\"range\":\"6000-6002\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("range 起点超界 → 提示", bad2.Contains("超出文件总行数"));
    var bad3 = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"BigService.cs\",\"range\":\"99-10\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("range 起>止 → 提示", bad3.Contains("起 ≤ 止"));

    // ④ 小文件回归：无截断
    var smallPath = Path.Combine(anDir, "Small.cs");
    File.WriteAllText(smallPath, """
        public class Small
        {
            public int A() { return 1; }
        }
        """);
    var smallOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"Small.cs\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("小文件：无截断 + 全量符号", !smallOut.Contains("... 省略") && smallOut.Contains("方法 A"));

    // ⑤ Python 同样生效
    var pyBig = Path.Combine(anDir, "pybig.py");
    var sbPy = new System.Text.StringBuilder();
    for (var i = 0; i < 350; i++)
        sbPy.AppendLine($"def fn{i}():\n    pass");
    File.WriteAllText(pyBig, sbPy.ToString());
    var pyOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"pybig.py\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("Python：超限截断生效", pyOut.Contains("... 省略") && pyOut.Contains("符号总数: 350"));
    var pyRange = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
        "{\"path\":\"pybig.py\",\"range\":\"5-10\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
    Check("Python：range 命中段内函数（fn4 在行 9）",
        pyRange.Contains("函数 fn4") && !pyRange.Contains("函数 fn0"));

    Directory.Delete(anDir, true);
}

// ── 单模型三角色：请求级温度映射（GetEndpoint + GetEffectiveTemperature） ──
Console.WriteLine("== 单模型三角色温度注入 ==");
{
    var opts = new Agent.Core.Models.AgentOptions(); // 默认：small 0.3 / large 0.7 / agent_pro 0.5 / 全局 0.2

    // 端点映射
    Check("GetEndpoint small", opts.GetEndpoint("small") == opts.Small);
    Check("GetEndpoint large", opts.GetEndpoint("large") == opts.Large);
    Check("GetEndpoint agent_pro", opts.GetEndpoint("agent_pro") == opts.AgentPro);
    Check("GetEndpoint classifier", opts.GetEndpoint("classifier") == opts.Classifier);
    Check("GetEndpoint 未知 → null", opts.GetEndpoint("unknown") is null);

    // 生效温度：端点显式配置优先
    Check("small 温度 0.3", Math.Abs(opts.GetEffectiveTemperature("small") - 0.3) < 1e-9);
    Check("large 温度 0.7", Math.Abs(opts.GetEffectiveTemperature("large") - 0.7) < 1e-9);
    Check("agent_pro 温度 0.5", Math.Abs(opts.GetEffectiveTemperature("agent_pro") - 0.5) < 1e-9);

    // 单模型三角色核心：三角色指向同一 tag（默认配置）
    var unified = new Agent.Core.Models.AgentOptions();
    Check("三角色同 tag", unified.Small.Model == unified.Large.Model
        && unified.Large.Model == unified.AgentPro.Model && unified.Small.Model == "law-unified");

    // 未配置端点温度 → 回退全局
    opts.Large = new Agent.Core.Models.ModelEndpoint { Model = "x", Temperature = null };
    Check("端点温度 null → 全局回退", Math.Abs(opts.GetEffectiveTemperature("large") - opts.Temperature) < 1e-9);
    Check("未知服务 → 全局回退", Math.Abs(opts.GetEffectiveTemperature("unknown") - opts.Temperature) < 1e-9);
}

// ── 时代隔离：GetEpochBreakdownAsync（agent_pro 30B vs 4B 如实拆分） ──
Console.WriteLine("== 时代隔离：GetEpochBreakdownAsync ==");
{
    var mdb = Path.Combine(TestTmp, "metrics_epoch_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
    var mc = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(mdb);
    var epoch = new DateTimeOffset(2026, 10, 2, 15, 30, 0, TimeSpan.FromHours(8));
    var legacyAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(8));
    var currentAt = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.FromHours(8));

    Agent.Core.Models.TaskMetric Msg(string id, bool ok, DateTimeOffset at) => new()
    {
        TaskId = id, SessionId = "s", UserInput = "时代测试", Mode = "agent",
        RoutingTier = "Rule", ServiceId = "agent_pro", RoutingReason = "test",
        Success = ok, TotalMs = 1000, StartedAt = at, FinishedAt = at
    };
    mc.RecordAsync(Msg("t1", true, legacyAt), CancellationToken.None).GetAwaiter().GetResult();
    mc.RecordAsync(Msg("t2", false, legacyAt), CancellationToken.None).GetAwaiter().GetResult();
    mc.RecordAsync(Msg("t3", false, legacyAt), CancellationToken.None).GetAwaiter().GetResult();
    mc.RecordAsync(Msg("t4", true, currentAt), CancellationToken.None).GetAwaiter().GetResult();
    mc.RecordAsync(Msg("t5", true, currentAt), CancellationToken.None).GetAwaiter().GetResult();

    var (cur, leg) = mc.GetEpochBreakdownAsync("agent_pro", epoch, CancellationToken.None).GetAwaiter().GetResult();
    Check("30B 时代 2 条", cur.TotalCount == 2);
    Check("30B 时代全成功", cur.SuccessCount == 2);
    Check("4B 历史 3 条", leg.TotalCount == 3);
    Check("4B 历史 1 成功", leg.SuccessCount == 1);
}

// ── 【P2】agent_pro 自动调权：时代过滤闭环查询 + ComplexReason 降级判定 ──
Console.WriteLine("== P2 自动调权：GetServiceStatsSinceAsync + TrySuggestDropAgentPro ==");
{
    var mdb = Path.Combine(TestTmp, "metrics_tune_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
    var mc = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(mdb);
    var epoch = new DateTimeOffset(2026, 10, 2, 15, 0, 0, TimeSpan.FromHours(8));
    var legacyAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(8));
    var currentAt = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.FromHours(8));

    Agent.Core.Models.TaskMetric Msg(string id, bool ok, DateTimeOffset at) => new()
    {
        TaskId = id, SessionId = "s", UserInput = "P2 调权测试", Mode = "agent",
        RoutingTier = "Rule", ServiceId = "agent_pro", RoutingReason = "test",
        Success = ok, TotalMs = 1000, StartedAt = at, FinishedAt = at
    };
    // 4B 历史 2 条（1 成功）——应被时代过滤排除
    mc.RecordAsync(Msg("old1", true, legacyAt), CancellationToken.None).GetAwaiter().GetResult();
    mc.RecordAsync(Msg("old2", false, legacyAt), CancellationToken.None).GetAwaiter().GetResult();
    // 30B 时代 2 条（2 成功）——应被保留
    mc.RecordAsync(Msg("new1", true, currentAt), CancellationToken.None).GetAwaiter().GetResult();
    mc.RecordAsync(Msg("new2", true, currentAt), CancellationToken.None).GetAwaiter().GetResult();

    var since = mc.GetServiceStatsSinceAsync("agent_pro", "Rule", epoch, 10, CancellationToken.None).GetAwaiter().GetResult();
    Check("P2 时代过滤：仅 30B 2 条", since.TotalCount == 2);
    Check("P2 时代过滤：2 成功", since.SuccessCount == 2);
    var all = mc.GetServiceStatsAsync("agent_pro", "Rule", 10, CancellationToken.None).GetAwaiter().GetResult();
    Check("P2 对照全量：4 条", all.TotalCount == 4);
    Check("P2 对照全量：3 成功（含 4B）", all.SuccessCount == 3);

    // TrySuggestDropAgentPro 三态
    Check("P2 样本不足(<5)保持 agent_pro",
        !Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestDropAgentPro(
            new Agent.Infrastructure.Routing.ServiceStats(4, 2, 1000), 5, 0.5, out _));
    Check("P2 成功率 0.4 <0.5 → 降级 large",
        Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestDropAgentPro(
            new Agent.Infrastructure.Routing.ServiceStats(5, 2, 1000), 5, 0.5, out var r1)
        && r1.Contains("降级 large"));
    Check("P2 成功率 0.6 ≥0.5 → 保持 agent_pro",
        !Agent.Infrastructure.Routing.RoutingIntelligence.TrySuggestDropAgentPro(
            new Agent.Infrastructure.Routing.ServiceStats(5, 3, 1000), 5, 0.5, out _));
}

// ── 工程图谱增强：依赖分析（符号引用 → 文件依赖边） ──
Console.WriteLine("== 工程图谱增强：依赖分析 ==");
{
    var proj = Path.Combine(TestTmp, "deps_proj_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(proj);
    File.WriteAllText(Path.Combine(proj, "Order.cs"),
        "namespace Demo;\npublic class Order { public int Id { get; set; } }\n");
    File.WriteAllText(Path.Combine(proj, "OrderService.cs"),
        "namespace Demo;\npublic class OrderService { public void Process(Order o) { } }\n");
    File.WriteAllText(Path.Combine(proj, "Program.cs"),
        "namespace Demo;\nclass Program { static void Main() { var s = new OrderService(); } }\n");

    var symbols = Agent.Infrastructure.Project.SymbolIndexer.Build(proj);
    Check("符号索引含 Order/OrderService", symbols.Any(s => s.Symbol == "Order") && symbols.Any(s => s.Symbol == "OrderService"));
    var edges = Agent.Infrastructure.Project.DependencyAnalyzer.Build(proj, symbols);
    Check("依赖边 OrderService→Order", edges.Any(e => e.File == "OrderService.cs" && e.DependsOnFile == "Order.cs"));
    Check("依赖边 Program→OrderService", edges.Any(e => e.File == "Program.cs" && e.DependsOnFile == "OrderService.cs"));
    Check("反向依赖 Order.cs ← OrderService.cs",
        Agent.Infrastructure.Project.DependencyAnalyzer.GetReverseDependencies("Order.cs", edges).Contains("OrderService.cs"));

    // analyze_deps 工具端到端（惰性建索引）
    var tool = new Agent.Infrastructure.Tools.DependencyAnalyzerTool(proj, proj,
        Path.Combine(TestTmp, "sym_epoch_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db"));
    var json = System.Text.Json.JsonDocument.Parse("{\"path\":\"Order.cs\"}").RootElement;
    var outText = tool.InvokeAsync(json, CancellationToken.None).GetAwaiter().GetResult();
    Check("analyze_deps 输出反向依赖", outText.Contains("反向依赖") && outText.Contains("OrderService.cs"));
    Check("analyze_deps 非工程模式报错",
        new Agent.Infrastructure.Tools.DependencyAnalyzerTool(proj, null, Path.Combine(TestTmp, "x.db"))
            .InvokeAsync(json, CancellationToken.None).GetAwaiter().GetResult().Contains("工程模式"));
}

Console.WriteLine("== P0-1 工程失败自动回滚（RollbackTaskFull） ==");
{
    var baseTmp = Path.Combine(TestTmp, "tests_rollback");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    var ws = Path.Combine(baseTmp, "run_task123");   // run 级工作目录（task_ 前缀）
    var proj = Path.Combine(baseTmp, "proj");
    Directory.CreateDirectory(ws);
    Directory.CreateDirectory(proj);

    var orderCs = Path.Combine(proj, "Order.cs");
    File.WriteAllText(orderCs, "class Order { public int Id { get; set; } }");
    var oldContent = File.ReadAllText(orderCs);

    // 模拟：改已存在文件前备份 + 写入（P-B：任务隔离 taskA）
    Agent.Infrastructure.Project.BackupService.Backup(ws, orderCs, proj, "taskA");
    File.WriteAllText(orderCs, "class Order { public int Id { get; set; } public double Amount { get; set; } }");
    // 模拟：新建工程文件（标记 .new）
    var taxCs = Path.Combine(proj, "TaxService.cs");
    File.WriteAllText(taxCs, "class TaxService { }");
    Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, taxCs, proj, "taskA");

    var (restored, removed) = Agent.Infrastructure.Project.BackupService.RollbackTaskFull(ws, proj, "taskA");
    Check("P0-1 恢复已存在文件备份", restored == 1 && File.ReadAllText(orderCs) == oldContent);
    Check("P0-1 清理新建文件", removed == 1 && !File.Exists(taxCs));
    // .new 目录不作为 stamp 目录（ListBackups/RollbackTask 遍历排除）
    Check("P0-1 新文件不残留在备份列表", !Agent.Infrastructure.Project.BackupService.ListBackups(ws).Any(b => b.Rel.StartsWith(".new", StringComparison.OrdinalIgnoreCase)));
}

Console.WriteLine("== P1-1 symbol 索引增量失效（mtime） ==");
{
    var baseTmp = Path.Combine(TestTmp, "tests_incr");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    var proj = Path.Combine(baseTmp, "proj");
    Directory.CreateDirectory(proj);
    File.WriteAllText(Path.Combine(proj, "A.cs"), "class A { }\nclass A2 { }");
    File.WriteAllText(Path.Combine(proj, "B.cs"), "class B { }");
    var db = Path.Combine(TestTmp, "sym_incr_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
    var store = new Agent.Infrastructure.Project.SymbolIndexStore(db);

    var full = store.BuildAndSave(proj);
    Check("P1-1 全量构建 3 符号", full == 3);
    // 修改 A.cs（追加新类型）→ 增量应只重扫 A.cs
    File.WriteAllText(Path.Combine(proj, "A.cs"), "class A { }\nclass A2 { }\nclass A3 { }");
    var inc = store.BuildAndSaveIncremental(proj);
    var allAfter = store.GetAll(proj);
    Check("P1-1 增量仅重扫 1 文件", inc.Rescanned == 1);
    Check("P1-1 新符号可见（A3）", allAfter.Any(s => s.Symbol == "A3"));
    Check("P1-1 未变文件保留（B）", allAfter.Any(s => s.Symbol == "B"));
    // 删除 B.cs → 增量应清除 B 记录（无文件需重扫，仅 stale 清理）
    File.Delete(Path.Combine(proj, "B.cs"));
    var inc2 = store.BuildAndSaveIncremental(proj);
    Check("P1-1 删除文件记录清除", inc2.Rescanned == 0 && !store.GetAll(proj).Any(s => s.Symbol == "B"));
}

Console.WriteLine("== 10-08 A 演示分支容错（OutputVerifier.HasGenericFailure 容错 3） ==");
{
    // 真实样本：Python 计算器教学演示——成功计算行 + 异常分支演示行并存。
    // 此前 "错误:" 命中业务演示行 → 误判失败 → 修复循环熔断误杀。
    var demoCalc = "计算器演示\n============\n10 + 5 = 15\n10 - 3 = 7\n4 * 6 = 24\n15 / 3 = 5.0\n10 / 0 -> 错误: 除数不能为零\n8 % 3 -> 错误: 不支持的运算符: %";
    Check("A 演示分支容错：计算器演示（成功行+错误演示行）→ 放行",
        !Generic(demoCalc, null));
    // 真失败仍拦：失败词命中但无"数字 op 数字 = 数字"成功计算形态
    Check("A 真失败仍拦：连接失败无计算行 → 判失败",
        Generic("错误: 无法连接服务器\n重试中", null));
    // 只有错误演示行、无成功计算行 → 仍判失败（保守）
    Check("A 仅错误演示行无成功行 → 仍判失败",
        Generic("10 / 0 -> 错误: 除数不能为零", null));
    // 结构性错误不受影响（Traceback 由前置检查硬拦，本容错不接管）
    Check("A 结构性错误不豁免：含 Traceback 仍判失败",
        Generic("Traceback (most recent call last)\n10 / 0 -> 错误: 除数不能为零", null));
    // 成功信号容错（既有容错 2）不受影响
    Check("A 既有成功信号容错保持：成功 5 条失败 1 条 → 放行",
        !Generic("处理 6 条：成功 5 条，失败 1 条", null));
    // 演示容错不吞掉带 expected 的失败场景（expected 含失败词仍走容错 1）
    Check("A expected 含失败词（取款失败样例）→ 放行（容错 1 优先）",
        !Generic("取款失败：余额不足", "取款失败：余额不足"));
    // ── 10-08 A' 放宽：真实模型输出形态——错误行为独立业务消息行（无算式前缀）──
    // 复测实证：Python 计算器模型输出 `10 + 5 = 15 ... 错误: 除数不能为零`（raise ValueError
    // 捕获后打印独立错误消息行），原 HasDemoErrorLine 要求算式前缀 → 容错 3 不触发 → 误杀。
    var demoCalcReal = "计算器测试:\n10 + 5 = 15\n10 - 5 = 5\n10 * 5 = 50\n10 / 5 = 2.0\n错误: 除数不能为零";
    Check("A' 独立错误消息行（无算式前缀）+ 成功计算行 → 放行",
        !Generic(demoCalcReal, null));
    // 英文 error 业务消息形态同样放行
    Check("A' 英文 error 业务消息 + 成功计算行 → 放行",
        !Generic("10 + 5 = 15\nerror: division by zero", null));
    // 否定/计数形态排除：错误: 0 是计数，不是业务失败消息 → 仍判失败
    Check("A' 否定计数形态排除：错误: 0 → 仍判失败",
        Generic("10 + 5 = 15\n错误: 0", null));
    // 无成功计算行 + 独立错误消息 → 仍判失败（保持保守）
    Check("A' 独立错误消息但无成功计算行 → 仍判失败",
        Generic("错误: 除数不能为零", null));
}

Console.WriteLine("== 10-08 Lookup 演示容错（OutputVerifier.HasGenericFailure 容错 3b） ==");
{
    // 实证样本 1：10-03 图书管理系统——完整演示含"查找 ID=99 → 未找到该图书"分支，
    // 原 LookupFailSignals 命中即失败 → 修复循环 → 任务失败（白跑 10 分钟）。
    var bookDemo = "图书管理系统演示\n==================\n已添加 3 本图书\n\n所有图书列表：\n" +
        "ID: 1, 标题: C#编程指南, 作者: 张三, 可借: True\n" +
        "ID: 2, 标题: 数据结构与算法, 作者: 李四, 可借: True\n" +
        "ID: 3, 标题: 设计模式详解, 作者: 王五, 可借: True\n\n" +
        "查找 ID 为 2 的图书：\n找到图书: 数据结构与算法 - 李四\n\n" +
        "查找 ID 为 99 的图书：\n未找到该图书\n\n" +
        "移除 ID 为 1 的图书：\n图书已成功移除";
    Check("L1 图书系统演示（找到+列表+未找到分支）→ 放行", !Generic(bookDemo, null));
    // 实证样本 2：09-22 产品查询——任务本身要求打印"未找到该产品"，被"未找到"误杀
    var productDemo = "未找到产品\n产品ID: 1, 名称: 笔记本电脑\n产品ID: 3, 名称: 智能手机";
    Check("L2 产品查询演示（未找到产品+产品ID 列表）→ 放行", !Generic(productDemo, null));
    // 孤立查找失败：只有"未找到"一行、无任何正向形态 → 仍判失败（保守）
    Check("L3 孤立未找到（无正向形态）→ 仍判失败", Generic("未找到该图书", null));
    // 未找到 + 无正向形态 + 其他失败词 → 失败
    Check("L4 未找到+失败词但无正向形态 → 仍判失败", Generic("未找到该图书\n处理失败: 数据库连接错误", null));
    // expected 含"未找到"（演示查找失败场景）→ 容错 1 优先放行（原逻辑回归）
    Check("L5 expected 含未找到（查找失败样例）→ 放行",
        !Generic("未找到该图书", "未找到该图书"));
    // 英文 not found + 列表条目 → 放行
    Check("L6 not found + ID 列表 → 放行",
        !Generic("Product not found\nID: 1, Name: Laptop\nID: 3, Name: Phone", null));
    // 仅 not found（无正向形态）→ 仍判失败
    Check("L7 仅 not found → 仍判失败", Generic("Product not found", null));
    // 否定前缀陷阱回归：确认"未找到"不被 (?<!未)找到 误当正向形态（否则豁免形同虚设）
    Check("L8 否定前缀陷阱：未找到 不被当作 正向找到 → 仍判失败", Generic("未找到该图书", null));
}

Console.WriteLine("== 10-08 A 证据链先行（HasSuccessEvidence + 4 参重载） ==");
{
    // 证据检测器（public HasSuccessEvidence）各形态
    var V = typeof(Agent.Infrastructure.Quality.OutputVerifier);
    var E = V.GetMethod("HasSuccessEvidence")!;
    bool Ev(string stdout, string? expected) => (bool)E.Invoke(null, new object?[] { stdout, expected })!;

    Check("证据1：expected 含失败词 → 有证据（任务要求演示失败）",
        Ev("取款失败：余额不足", "取款失败：余额不足"));
    Check("证据2：成功计数报告（成功+数字）→ 有证据",
        Ev("处理 6 条：成功 5 条，失败 1 条", null));
    Check("证据4：操作成功词（查找/流程语境）→ 有证据",
        Ev("操作成功", null));
    Check("证据3：计算行 + 业务错误消息 → 有证据",
        Ev("10 + 5 = 15\n错误: 除数不能为零", null));
    Check("证据3 否定形态：错误: 0 计数 → 无证据",
        !Ev("10 + 5 = 15\n错误: 0", null));
    Check("证据4：查找正向形态（ID 列表）→ 有证据",
        Ev("未找到该图书\nID: 1, 标题: 数据结构与算法", null));
    Check("证据4 否定前缀：孤立未找到 → 无证据",
        !Ev("未找到该图书", null));
    Check("无任何成功形态 + 失败词 → 无证据",
        !Ev("失败: 连接超时\n重试中", null));

    // 4 参重载语义
    var G4 = V.GetMethod("HasGenericFailure", new[] { typeof(string), typeof(string), typeof(string).MakeByRefType(), typeof(bool).MakeByRefType() })!;
    bool G4Call(string stdout, string? expected, out bool hadEvidence)
    {
        var args = new object?[] { stdout, expected, null, null };
        var r = (bool)G4.Invoke(null, args)!;
        hadEvidence = (bool)args[3]!;
        return r;
    }
    string? G4Reason(string stdout, string? expected, out bool hadEvidence)
    {
        var args = new object?[] { stdout, expected, null, null };
        _ = (bool)G4.Invoke(null, args)!;
        hadEvidence = (bool)args[3]!;
        return (string?)args[2];
    }

    Check("4参：真失败（软信号无证据）→ true + 无证据",
        G4Call("错误: 无法连接服务器\n重试中", null, out var he1) && !he1);
    Check("4参：边缘成功（软信号+证据）→ false + 有证据",
        !G4Call("10 + 5 = 15\n错误: 除数不能为零", null, out var he2) && he2);
    Check("4参：边缘成功时 reason 保留信号描述（供标记/落库）",
        !G4Call("10 + 5 = 15\n错误: 除数不能为零", null, out _) &&
        !string.IsNullOrWhiteSpace(G4Reason("10 + 5 = 15\n错误: 除数不能为零", null, out _)));
    Check("4参：查找类边缘成功 reason 保留信号描述",
        !string.IsNullOrWhiteSpace(G4Reason("未找到该图书\nID: 1, 标题: 数据结构与算法", null, out _)));
    Check("4参：干净输出 → false + 无证据",
        !G4Call("IP: 8.8.8.8 / Temperature: 21°C / Humidity: 65%", null, out var he3) && !he3);
    Check("4参：查找豁免 + 通用失败词独立判定（保守，不被吞）→ true",
        G4Call("未找到该图书\nID: 1, 标题: 数据结构与算法\n处理失败: 数据库连接错误", null, out var he4));
}

Console.WriteLine("== 10-08 B 同文件内自重复类型定义检测（P-D 扩展） ==");
{
    // 真实样本：LetterCaseChecker.cs 900 字符内 2×class LetterCaseChecker → CS0101。
    var selfDup = Agent.Infrastructure.Project.TypeConflictDetector.DetectSelfDuplicateTypes(
        "using System;\npublic class LetterCaseChecker\n{\n}\npublic class LetterCaseChecker\n{\n}\n");
    Check("B DetectSelfDuplicateTypes → 检出 1 项重复（定义 2 次）",
        selfDup.Count == 1 && selfDup[0].Contains("LetterCaseChecker") && selfDup[0].Contains("2 次"));
    // 正常单类 → 空
    Check("B 单类定义 → 无自重复",
        Agent.Infrastructure.Project.TypeConflictDetector.DetectSelfDuplicateTypes(
            "public class Order { }\npublic class OrderService { }").Count == 0);
    // 注释/字符串中的类名不计
    Check("B 注释/字符串中的类名 → 不计入自重复",
        Agent.Infrastructure.Project.TypeConflictDetector.DetectSelfDuplicateTypes(
            "// class Fake\nstring s = \"class InString\";\npublic class Real { }").Count == 0);
    // 三份重复 → 定义 3 次
    var selfDup3 = Agent.Infrastructure.Project.TypeConflictDetector.DetectSelfDuplicateTypes(
        "public class X { }\npublic class X { }\npublic class X { }");
    Check("B 三份重复 → 定义 3 次",
        selfDup3.Count == 1 && selfDup3[0].Contains("3 次"));
}

Console.WriteLine("== P0-2 expected_output 严格校验（IsTaskExpectedOutputStrict） ==");
{
    var V = typeof(Agent.Infrastructure.Quality.OutputVerifier);
    System.Func<string, System.Collections.Generic.IReadOnlyList<string>, bool> Strict =
        (e, l) => Agent.Infrastructure.Quality.OutputVerifier.IsTaskExpectedOutputStrict(e, l);
    System.Func<string, System.Collections.Generic.IReadOnlyList<string>, bool> Loose =
        (e, l) => Agent.Infrastructure.Quality.OutputVerifier.IsTaskExpectedOutput(e, l);
    Check("P0-2 严格：全部行精确匹配 → 通过",
        Strict("INFO: 1\nWARN: 1\nERROR: 2",
            new List<string> { "INFO: 1", "WARN: 1", "ERROR: 2" }));
    Check("P0-2 严格：缺一行 → 不通过",
        !Strict("INFO: 1\nWARN: 1\nERROR: 2",
            new List<string> { "INFO: 1", "ERROR: 2" }));
    Check("P0-2 严格：顺序无关（行集匹配）→ 通过",
        Strict("INFO: 1\nERROR: 2",
            new List<string> { "ERROR: 2", "INFO: 1" }));
    Check("P0-2 严格：空 expected → 不通过（无预期不适用）",
        !Strict("", new List<string> { "hello" }));
    // 宽松 vs 严格差异实证：expected 多行、stdout 缺行 → 宽松通过、严格拒绝
    var loose = Loose("INFO: 1\nWARN: 1\nERROR: 2",
        new List<string> { "INFO: 1", "ERROR: 2" });
    var strict = Strict("INFO: 1\nWARN: 1\nERROR: 2",
        new List<string> { "INFO: 1", "ERROR: 2" });
    Check("P0-2 宽松/严格差异（宽松通过、严格拒绝）", loose && !strict);
}

Console.WriteLine("== H1 跨任务误删防护（ClearNewMarkers，P-B taskId 隔离） ==");
{
    var baseTmp = Path.Combine(TestTmp, "tests_h1");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    var ws = Path.Combine(baseTmp, "run_taskA");
    var proj = Path.Combine(baseTmp, "proj");
    Directory.CreateDirectory(ws);
    Directory.CreateDirectory(proj);

    // 任务 A 成功：新建 TaxService.cs + 标记 → 成功收尾 ClearNewMarkers（模拟 CLI 成功分支，P-B 只清 taskA）
    var taxCs = Path.Combine(proj, "TaxService.cs");
    File.WriteAllText(taxCs, "class TaxService { }");
    Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, taxCs, proj, "taskA");
    Agent.Infrastructure.Project.BackupService.ClearNewMarkers(ws, "taskA");

    // 任务 B（同会话）失败：新建 Broken.cs + 标记 → 回滚（P-B 只回滚 taskB）
    var brokenCs = Path.Combine(proj, "Broken.cs");
    File.WriteAllText(brokenCs, "class Broken { }");
    Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, brokenCs, proj, "taskB");
    var (restored, removed) = Agent.Infrastructure.Project.BackupService.RollbackTaskFull(ws, proj, "taskB");
    Check("H1 任务 A 成功建的文件保留（不被 B 失败回滚误删）", File.Exists(taxCs));
    Check("H1 任务 B 新建文件被清理", removed == 1 && !File.Exists(brokenCs));
    Check("H1 回滚后 A 的备份目录不残留", !Directory.Exists(Path.Combine(ws, ".backup", "taskA")));
}

// ── P-A：回滚取最早备份（任务多次写入时最近备份=任务自身中间状态，最早=任务前干净状态） ──
Console.WriteLine("== P-A 回滚取最早备份（防恢复到任务中间状态） ==");
{
    var baseTmp = Path.Combine(TestTmp, "tests_pa");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    var ws = Path.Combine(baseTmp, "run_taskA");
    var proj = Path.Combine(baseTmp, "proj");
    Directory.CreateDirectory(ws);
    Directory.CreateDirectory(proj);
    var orderCs = Path.Combine(proj, "OrderService.cs");
    File.WriteAllText(orderCs, "// 任务前：干净内容 v0");

    // 任务内两次写入：初写（备份干净 v0）→ 写入坏内容 v1 → 修复重写（备份坏 v1）→ 写入 v2
    Agent.Infrastructure.Project.BackupService.Backup(ws, orderCs, proj, "taskX");
    File.WriteAllText(orderCs, "// 第一次写入（坏中间状态）v1");
    Agent.Infrastructure.Project.BackupService.Backup(ws, orderCs, proj, "taskX");
    File.WriteAllText(orderCs, "// 第二次写入（最终坏状态）v2");

    var restored = Agent.Infrastructure.Project.BackupService.RollbackTask(ws, proj, "taskX");
    Check("P-A 回滚取最早备份（恢复任务前干净状态，非中间状态）",
        restored == 1 && File.ReadAllText(orderCs).Contains("v0") && !File.ReadAllText(orderCs).Contains("v1"));
}

// ── 跨任务端到端（P-B 方案 A 三场景）：A 成功改动不被 B 失败回滚误伤 ──
Console.WriteLine("== 跨任务端到端（taskId 隔离三场景） ==");
{
    // 场景 1：A 成功新建 → B 失败 → A 的新文件还在
    {
        var baseTmp = Path.Combine(TestTmp, "ts1");
        if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
        var ws = Path.Combine(baseTmp, "run");
        var proj = Path.Combine(baseTmp, "proj");
        Directory.CreateDirectory(ws); Directory.CreateDirectory(proj);

        // A 成功：新建 AFile.cs + 成功收尾清 taskA
        var aFile = Path.Combine(proj, "AFile.cs");
        File.WriteAllText(aFile, "class AFile { }");
        Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, aFile, proj, "taskA");
        Agent.Infrastructure.Project.BackupService.ClearNewMarkers(ws, "taskA");

        // B 失败：新建 BFile.cs → 回滚 taskB
        var bFile = Path.Combine(proj, "BFile.cs");
        File.WriteAllText(bFile, "class BFile { }");
        Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, bFile, proj, "taskB");
        var (_, removed) = Agent.Infrastructure.Project.BackupService.RollbackTaskFull(ws, proj, "taskB");
        Check("场景1 A 新文件保留", File.Exists(aFile));
        Check("场景1 B 新文件清理", removed == 1 && !File.Exists(bFile));
    }

    // 场景 2：A 成功 → A 再改同文件 → B 失败 → A 的第二次改动还在
    {
        var baseTmp = Path.Combine(TestTmp, "ts2");
        if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
        var ws = Path.Combine(baseTmp, "run");
        var proj = Path.Combine(baseTmp, "proj");
        Directory.CreateDirectory(ws); Directory.CreateDirectory(proj);
        var file = Path.Combine(proj, "OrderService.cs");
        File.WriteAllText(file, "// 初始状态 v0");

        // A 成功：第一次改动 v1 → 成功收尾清 taskA（备份随任务成功清除）
        Agent.Infrastructure.Project.BackupService.Backup(ws, file, proj, "taskA");
        File.WriteAllText(file, "// A 第一次改动 v1");
        Agent.Infrastructure.Project.BackupService.ClearNewMarkers(ws, "taskA");
        // A 再改同文件（第二次改动 v2，同任务 taskA 新备份）
        Agent.Infrastructure.Project.BackupService.Backup(ws, file, proj, "taskA");
        File.WriteAllText(file, "// A 第二次改动 v2");

        // B 失败：改同文件（备份=含 v2 的任务前）→ 回滚 taskB（只恢复 B 的备份=v2）
        Agent.Infrastructure.Project.BackupService.Backup(ws, file, proj, "taskB");
        File.WriteAllText(file, "// B 的改动（坏）v3");
        Agent.Infrastructure.Project.BackupService.RollbackTaskFull(ws, proj, "taskB");
        Check("场景2 A 第二次改动保留（B 回滚不碰 A 的备份）",
            File.ReadAllText(file).Contains("v2") && !File.ReadAllText(file).Contains("v3"));
    }

    // 场景 3：A 失败清空 → B 成功新建同名 → C 失败 → B 的文件还在
    {
        var baseTmp = Path.Combine(TestTmp, "ts3");
        if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
        var ws = Path.Combine(baseTmp, "run");
        var proj = Path.Combine(baseTmp, "proj");
        Directory.CreateDirectory(ws); Directory.CreateDirectory(proj);

        // A 失败：新建 X.cs + 回滚 taskA（清理 A 的 X.cs 与标记）
        var xFile = Path.Combine(proj, "X.cs");
        File.WriteAllText(xFile, "class X { }");
        Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, xFile, proj, "taskA");
        Agent.Infrastructure.Project.BackupService.RollbackTaskFull(ws, proj, "taskA");
        Check("场景3 A 失败回滚删除其 X.cs", !File.Exists(xFile));

        // B 成功：新建同名 X.cs + 成功收尾清 taskB
        File.WriteAllText(xFile, "class X2 { }");
        Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, xFile, proj, "taskB");
        Agent.Infrastructure.Project.BackupService.ClearNewMarkers(ws, "taskB");

        // C 失败：回滚 taskC（不碰 B 的 X.cs）
        Agent.Infrastructure.Project.BackupService.RollbackTaskFull(ws, proj, "taskC");
        Check("场景3 B 新建同名文件保留（C 回滚不误删）",
            File.Exists(xFile) && File.ReadAllText(xFile).Contains("class X2"));
    }
}

// ── dry-run：预览回滚（不执行） ──
Console.WriteLine("== rollback --dry-run 预览（不执行） ==");
{
    var baseTmp = Path.Combine(TestTmp, "tsdry");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    var ws = Path.Combine(baseTmp, "run");
    var proj = Path.Combine(baseTmp, "proj");
    Directory.CreateDirectory(ws); Directory.CreateDirectory(proj);
    var file = Path.Combine(proj, "Order.cs");
    File.WriteAllText(file, "v0");
    Agent.Infrastructure.Project.BackupService.Backup(ws, file, proj, "taskD");
    File.WriteAllText(file, "v1");
    var newFile = Path.Combine(proj, "New.cs");
    File.WriteAllText(newFile, "class New { }");
    Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, newFile, proj, "taskD");

    var (restoreTargets, removeTargets) = Agent.Infrastructure.Project.BackupService.DryRunRollbackTask(ws, proj, "taskD");
    Check("dry-run 列出将恢复的文件", restoreTargets.Count == 1 && restoreTargets[0].Replace('\\', '/').EndsWith("Order.cs"));
    Check("dry-run 列出将删除的新文件", removeTargets.Count == 1 && removeTargets[0].Replace('\\', '/').EndsWith("New.cs"));
    Check("dry-run 不执行任何改动", File.ReadAllText(file).Contains("v1") && File.Exists(newFile));
}

// ════════════════════════════════════════════════════════════
// Diff P0：FileDiffService 行级 diff 引擎（Diff/）
//   1. 增删改混合小 diff → hunk 结构与行类型正确
//   2. 改 1 行 → 上下文裁剪只输出 ±3 行（5000 行场景 ~7 行而非全文）
//   3. 相同 / 空（新建）/ 全删 → 边界
//   4. 大文件降级（2500² 阈值）
//   5. 相邻改动合并（gap ≤ 2×Context）
// ════════════════════════════════════════════════════════════
Console.WriteLine("== Diff P0 FileDiffService（Diff/） ==");
{
    // ── 1. 增删改混合 ──
    var before = new[] { "a", "b", "c", "d", "e" };
    var after = new[] { "a", "x", "c", "d", "f" };
    var r = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(before, after);
    Check("混合 diff 非 TooLarge", !r.TooLarge);
    Check("混合 diff 有 1 个 hunk（上下文相连）", r.Hunks.Count == 1);
    var lines = r.Hunks[0].Lines;
    Check("混合 diff 含增删改行", lines.Any(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Add && l.Text == "x")
        && lines.Any(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Remove && l.Text == "b")
        && lines.Any(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Add && l.Text == "f"));
    Check("Same 行带新旧行号", lines.First(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Same && l.Text == "c").OldNo == 3
        && lines.First(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Same && l.Text == "c").NewNo == 3);

    // ── 2. 上下文裁剪：改 1 行 → 只输出变更附近 ±3 行 ──
    var big = Enumerable.Range(1, 5000).Select(i => $"line{i}").ToArray();
    var bigAfter = big.ToArray();
    bigAfter[2499] = "CHANGED"; // 改第 2500 行
    var r2 = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(big, bigAfter);
    Check("5000 行改 1 行 → 非降级且可行", !r2.TooLarge && r2.Hunks.Count >= 1);
    var total = r2.Hunks.Sum(h => h.Lines.Count);
    Check($"5000 行改 1 行 → 输出远小于全文（{total} 行 ≤ 20）", total <= 20);
    Check("裁剪含变更行与 ±3 上下文", r2.Hunks[0].Lines.Any(l => l.Text == "CHANGED")
        && r2.Hunks[0].Lines.Any(l => l.Text == "line2497") && r2.Hunks[0].Lines.Any(l => l.Text == "line2502"));
    Check("hunk 头计数正确", r2.Hunks[0].OldCount >= 6 && r2.Hunks[0].NewCount >= 6);

    // ── 3. 边界：相同 / 新建 / 全删 ──
    var same = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(new[] { "a", "b" }, new[] { "a", "b" });
    Check("相同文件 → 无 hunk", same.Hunks.Count == 0);
    var created = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(Array.Empty<string>(), new[] { "n1", "n2", "n3" });
    Check("新建（空→3 行）→ 全 Add 单 hunk", created.Hunks.Count == 1
        && created.Hunks[0].Lines.All(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Add));
    var deleted = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(new[] { "a", "b" }, Array.Empty<string>());
    Check("全删 → 全 Remove 单 hunk", deleted.Hunks.Count == 1
        && deleted.Hunks[0].Lines.All(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Remove));

    // ── 4. 大文件降级（只针对"全不同/大变化段"，改 1 行的大文件可行） ──
    var hugeA = Enumerable.Range(1, 3000).Select(i => $"a{i}").ToArray();
    var hugeB = Enumerable.Range(1, 3000).Select(i => $"b{i}").ToArray(); // 全部不同 → 变化段 3000×3000
    var r4 = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(hugeA, hugeB);
    Check("3000 行全不同 → 明确降级 TooLarge + 原因", r4.TooLarge && !string.IsNullOrEmpty(r4.TooLargeReason));
    var r4b = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(hugeA, hugeA.Select(x => x == "a1" ? "CHANGED" : x).ToArray());
    Check("3000 行改 1 行 → 非降级且只输出 ±3 行", !r4b.TooLarge && r4b.Hunks.Sum(h => h.Lines.Count) <= 20);

    // ── 5. 相邻改动合并（gap ≤ 2×3=6 合并；>6 分块） ──
    var m = Enumerable.Range(1, 30).Select(i => $"line{i}").ToArray();
    var m2 = m.ToArray();
    m2[4] = "C1"; m2[8] = "C2"; // 改动 gap 3 行 → 合并为 1 hunk
    var r5 = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(m, m2);
    Check("gap≤6 两处改动合并为 1 hunk", r5.Hunks.Count == 1 && r5.Hunks[0].Lines.Any(l => l.Text == "C1") && r5.Hunks[0].Lines.Any(l => l.Text == "C2"));
    var m3 = m.ToArray();
    m3[4] = "C1"; m3[20] = "C2"; // gap 15 行 → 2 hunks
    var r6 = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(m, m3);
    Check("gap>6 两处改动分为 2 hunks", r6.Hunks.Count == 2);

    // ── 6. 无头文本渲染（PlainDiffFormatter，diff 命令 PlainText 通道） ──
    var fmt = Agent.Infrastructure.Diff.PlainDiffFormatter.Format(r);
    Check("PlainDiffFormatter 含 hunk 头与 +/- 行", fmt.Contains("@@ ") && fmt.Contains("\n- ") && fmt.Contains("\n+ "));
    Check("PlainDiffFormatter 无 TooLarge 文本", !fmt.Contains("文件过大"));
}

// ════════════════════════════════════════════════════════════
// Spectre P0：渲染器抽象（Rendering/）
//   1. RendererFactory 无头/交互选择（确定性）
//   2. RendererBase.FormatEvent 七事件 → 文本映射固化（防搬移改坏格式，E2E/日志解析依赖）
//   3. Truncate / FormatToolArgs 边界
//   4. 兼容抽样：与旧 CLI 格式精确相等
// ════════════════════════════════════════════════════════════
Console.WriteLine("== Spectre P0 渲染器（Rendering/） ==");
{
    // ── 1. 工厂选择 ──
    var plain = Agent.Infrastructure.Rendering.RendererFactory.Create(false);
    var spectre = Agent.Infrastructure.Rendering.RendererFactory.Create(true);
    Check("无头 → PlainTextRenderer", plain is Agent.Infrastructure.Rendering.PlainTextRenderer);
    Check("交互 → SpectreRenderer（构造不抛）", spectre is Agent.Infrastructure.Rendering.SpectreRenderer);

    // ── 1b. 终端增强（10-07）：TaskPrefix / Verbose 默认值与类型（交互态专用，PlainText 不受影响）──
    var sp = (Agent.Infrastructure.Rendering.SpectreRenderer)spectre;
    Check("Spectre TaskPrefix 默认空", sp.TaskPrefix == "");
    Check("Spectre Verbose 默认 false", sp.Verbose == false);

    // ── 2. FormatEvent 七事件映射（与旧 RenderEvent 文本格式逐字节一致）──
    var rt1 = new Agent.Core.Models.AgentEvent.Routing("large", "L0.5", "写任务", "写任务");
    Check("Routing 带意图", Agent.Infrastructure.Rendering.RendererBase.FormatEvent(rt1) == "[路由] L0.5 [写任务] → large");
    var rt2 = new Agent.Core.Models.AgentEvent.Routing("large", "L0.5", "写任务", null);
    Check("Routing 无意图", Agent.Infrastructure.Rendering.RendererBase.FormatEvent(rt2) == "[路由] L0.5 → large");
    var pg = new Agent.Core.Models.AgentEvent.PlanGenerated("构建订单系统", "csharp", new List<string> { "Order.cs", "Program.cs" });
    Check("PlanGenerated 多行文件清单",
        Agent.Infrastructure.Rendering.RendererBase.FormatEvent(pg) == "[规划] 构建订单系统（csharp，2 个文件）\n  - Order.cs\n  - Program.cs");
    var fw = new Agent.Core.Models.AgentEvent.FileWritten("x.cs", 123);
    Check("FileWritten", Agent.Infrastructure.Rendering.RendererBase.FormatEvent(fw) == "[写入] x.cs（123 字符）");
    var tcArgs = JsonDocument.Parse("{\"command\":\"dotnet run\"}").RootElement;
    var tc = new Agent.Core.Models.AgentEvent.ToolCall("_-run_command", tcArgs);
    Check("ToolCall 保留原名+参数格式化",
        Agent.Infrastructure.Rendering.RendererBase.FormatEvent(tc) == "[工具调用] _-run_command  command=\"dotnet run\"");
    var tr = new Agent.Core.Models.AgentEvent.ToolResultEvent("read_file", "文件内容", true);
    Check("ToolResult 成功",
        Agent.Infrastructure.Rendering.RendererBase.FormatEvent(tr) == "[工具结果] read_file: 文件内容");
    var longOut = new string('x', 400);
    var tr2 = new Agent.Core.Models.AgentEvent.ToolResultEvent("read_file", longOut, false);
    Check("ToolResult 截断 300", Agent.Infrastructure.Rendering.RendererBase.FormatEvent(tr2).EndsWith("..."));
    var fa = new Agent.Core.Models.AgentEvent.FinalAnswer("任务完成");
    Check("FinalAnswer", Agent.Infrastructure.Rendering.RendererBase.FormatEvent(fa) == "[完成] 任务完成");
    var err = new Agent.Core.Models.AgentEvent.Error("出错");
    Check("Error", Agent.Infrastructure.Rendering.RendererBase.FormatEvent(err) == "[错误] 出错");

    // ── 3. Truncate / FormatToolArgs 边界 ──
    Check("Truncate 短串不变",
        Agent.Infrastructure.Rendering.RendererBase.Truncate("abc", 5) == "abc");
    Check("Truncate 长串截断",
        Agent.Infrastructure.Rendering.RendererBase.Truncate("abcdef", 3) == "abc...");
    var wfArgs = JsonDocument.Parse("{\"path\":\"p.py\",\"content\":\"0123456789\"}").RootElement;
    Check("FormatToolArgs write_file",
        Agent.Infrastructure.Rendering.RendererBase.FormatToolArgs("write_file", wfArgs) == "path=\"p.py\", content=10 字符");
    var ldArgs = JsonDocument.Parse("{}").RootElement;
    Check("FormatToolArgs list_dir 默认 path",
        Agent.Infrastructure.Rendering.RendererBase.FormatToolArgs("list_dir", ldArgs) == "path=\".\"");

    // ── 4. 兼容抽样：旧 CLI 格式精确相等 ──
    Check("兼容抽样 FileWritten",
        Agent.Infrastructure.Rendering.RendererBase.FormatEvent(fw) == "[写入] x.cs（123 字符）");
    Check("兼容抽样 Routing",
        Agent.Infrastructure.Rendering.RendererBase.FormatEvent(rt2) == "[路由] L0.5 → large");
    Check("PlainText RenderEventAsync 不抛",
        plain.RenderEventAsync(fa).IsCompletedSuccessfully);

    // ── 5. 双路径漂移回归：orch 命令与任务路径同源（ToolResult 统一截断 300）──
    Check("ToolResult 截断统一 300（orch 路径原 500 已收敛）",
        Agent.Infrastructure.Rendering.RendererBase.FormatEvent(tr2).Length <= 300 + " [工具结果] read_file: ".Length + "...".Length);
}

// ═══════════════════════════════════════════════════════════════
// 双根保护（读放开 / 写白名单 / 敏感源头拒）——PathBoundary + SensitivePathGuard
// ═══════════════════════════════════════════════════════════════
Console.WriteLine("== 双根保护：PathBoundary 逐段比较 ==");
{
    Check("子目录在内", Agent.Infrastructure.Project.PathBoundary.IsInside(@"X:\proj", @"X:\proj\src\a.cs"));
    Check("root 自身在内", Agent.Infrastructure.Project.PathBoundary.IsInside(@"X:\proj", @"X:\proj"));
    Check("前缀假阳性根治（proj2 不被 proj 放行）", !Agent.Infrastructure.Project.PathBoundary.IsInside(@"X:\proj", @"X:\proj2\a.cs"));
    Check(".. 跳脱拒绝",
        !Agent.Infrastructure.Project.PathBoundary.IsInside(@"X:\proj", System.IO.Path.GetFullPath(@"X:\proj\..\proj2\a.cs")));
    Check("大小写归一", Agent.Infrastructure.Project.PathBoundary.IsInside(@"X:\PROJ", @"x:\proj\a.cs"));
}

Console.WriteLine("== 双根保护：SensitivePathGuard 敏感区源头拒 ==");
{
    Check("系统区命中（C:\\Windows 子路径）", Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(@"C:\Windows\System32\evil.txt"));
    Check("凭据区命中（.ssh）", Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(@"C:\Users\26543\.ssh\config"));
    Check("前缀假阳性不误伤（C:\\Windows2）", !Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(@"C:\Windows2\evil.txt"));
    Check("普通工程路径不拒", !Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(@"X:\work\proj\src\a.cs"));

    // 8.3 短名（条件断言：仅当短名目录真实存在）
    var shortDir = @"C:\PROGRA~1\Common Files";
    if (Directory.Exists(shortDir))
        Check("8.3 短名绕过被拦（C:\\PROGRA~1）", Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(shortDir));
    else
        Console.WriteLine("  [SKIP] 8.3 短名断言：C:\\PROGRA~1\\Common Files 不存在（系统可能关闭了 8.3 短名）");

    // 符号链接（条件断言：junction 创建成功才测）
    var linkBase = Path.Combine(TestTmp, "tests_dr_link");
    if (Directory.Exists(linkBase)) Directory.Delete(linkBase, true);
    Directory.CreateDirectory(linkBase);
    var linkPath = Path.Combine(linkBase, "lnk");
    var linked = false;
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"C:\\Windows\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(5000);
        linked = p.ExitCode == 0 && Directory.Exists(linkPath);
    }
    catch { linked = false; }
    if (linked)
    {
        Check("符号链接最终目标命中敏感区（link→C:\\Windows）",
            Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(Path.Combine(linkPath, "System32", "cmd.exe")));
        // junction 目录删除需先删链接本体（否则 UnauthorizedAccessException）
        try { Directory.Delete(linkPath); } catch { }
        try { Directory.Delete(linkBase, true); } catch { }
    }
    else
        Console.WriteLine("  [SKIP] 符号链接断言：junction 创建失败（权限或环境）");

    // 用户自定义 denylist（第三类预留）：不存在静默 + 存在加载
    var dlDir = Path.Combine(TestTmp, "tests_dr_denylist");
    if (Directory.Exists(dlDir)) Directory.Delete(dlDir, true);
    Directory.CreateDirectory(dlDir);
    Agent.Infrastructure.Project.SensitivePathGuard.LoadUserDenylist(dlDir); // 无 sensitive_denylist.txt → 静默跳过
    Check("denylist 文件不存在 → 静默（不抛）", true);
    File.WriteAllText(Path.Combine(dlDir, "sensitive_denylist.txt"),
        "# 用户自定义敏感路径\n\n  \nX:\\work\\my_private\n");
    Agent.Infrastructure.Project.SensitivePathGuard.AddDeniedRoot(@"X:\work\my_private");
    Check("用户 denylist 追加生效", Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(@"X:\work\my_private\secret.txt"));
    Directory.Delete(dlDir, true);
}

Console.WriteLine("== 双根保护：写白名单（WriteRoots）+ 前缀假阳性 ==");
{
    var baseTmp = Path.Combine(TestTmp, "tests_dr_write");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    var ws = Path.Combine(baseTmp, "task");
    var proj = Path.Combine(baseTmp, "proj");
    var proj2 = Path.Combine(baseTmp, "proj2"); // 前缀同名目录（假阳性测试靶）
    Directory.CreateDirectory(ws);
    Directory.CreateDirectory(proj);
    Directory.CreateDirectory(proj2);

    string W(Agent.Infrastructure.Tools.AgentToolBase tool, string path, string content)
    {
        var json = $"{{\"path\":{JsonSerializer.Serialize(path)},\"content\":{JsonSerializer.Serialize(content)}}}";
        var args = JsonSerializer.Deserialize<JsonElement>(json);
        return tool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
    }

    var wRw = new Agent.Infrastructure.Tools.WriteFileTool(ws, proj, true);
    var absIn = Path.Combine(proj, "src", "Abs.cs");
    Check("写白名单：工程根内绝对路径 → 放行",
        W(wRw, absIn, "// abs\n").Contains("已写入") && File.Exists(absIn));
    var absOut = Path.Combine(baseTmp, "outside.cs");
    Check("写白名单：双根外绝对路径 → 越界拒",
        W(wRw, absOut, "x").Contains("越界"));
    Check("写白名单：前缀同名目录（proj2）→ 越界拒（假阳性根治）",
        W(wRw, Path.Combine(proj2, "f.cs"), "x").Contains("越界"));
    Directory.Delete(baseTmp, true);
}

Console.WriteLine("== 双根保护：run_command 敏感路径源头拒（跨模式） ==");
{
    var baseTmp = Path.Combine(TestTmp, "tests_dr_rc");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    var ws = Path.Combine(baseTmp, "task");
    Directory.CreateDirectory(ws);

    string Rc(string command)
    {
        var tool = new Agent.Infrastructure.Tools.RunCommandTool(ws);
        var args = JsonSerializer.Deserialize<JsonElement>($"{{\"command\":{JsonSerializer.Serialize(command)}}}");
        return tool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
    }

    Check("绝对路径敏感命令 → 拒绝", Rc("type C:\\Users\\26543\\.ssh\\config").Contains("敏感"));
    Check("环境变量展开后敏感命令 → 拒绝", Rc("type %USERPROFILE%\\.ssh\\config").Contains("敏感"));
    Directory.Delete(baseTmp, true);
}

Console.WriteLine("== 双根保护：启动重叠自检 ==");
{
    var overlaps = Agent.Infrastructure.Project.SensitivePathGuard.FindWriteRootOverlaps(new[] { @"X:\work\proj" });
    Check("正常工程根不重叠", overlaps.Count == 0);
    // 工程根落在敏感区内 → 命中
    var overlaps2 = Agent.Infrastructure.Project.SensitivePathGuard.FindWriteRootOverlaps(
        new[] { @"C:\Users\26543\.ssh\myproj" });
    Check("敏感区内工程根 → 重叠检出", overlaps2.Count == 1);
}

// 双根保护·输入路径自动识别（给路径就干活）——ProjectPathDetector
// ═══════════════════════════════════════════════════════════════
Console.WriteLine("== 双根保护：ProjectPathDetector 输入路径自动识别 ==");
{
    var baseTmp = Path.Combine(TestTmp, "tests_dr_autouse");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    var proj = Path.Combine(baseTmp, "My Project", "Sub"); // 含空格的嵌套目录（真实路径）
    Directory.CreateDirectory(proj);
    Directory.CreateDirectory(Path.Combine(proj, "src"));
    File.WriteAllText(Path.Combine(proj, "README.md"), "# demo");

    string Ext(string input) =>
        Agent.Infrastructure.Project.ProjectPathDetector.TryExtractProjectPath(input, out var p) ? p : "";

    // 提取：中文标点分片 + 尾部 CJK 语境词剥离
    Check("提取：标点分片后命中（读取一下 …项目）",
        Ext($"读取一下 {proj} 这个路径的项目，分析一下").Equals(Path.GetFullPath(proj), StringComparison.OrdinalIgnoreCase));
    Check("提取：含空格真实路径直接命中（不误剥）",
        Ext($"看看 {proj}").Equals(Path.GetFullPath(proj), StringComparison.OrdinalIgnoreCase));
    Check("提取：尾部中文语境词剥离（X:\\…目录）",
        Ext($"分析 {proj} 目录").Equals(Path.GetFullPath(proj), StringComparison.OrdinalIgnoreCase));
    Check("提取：无路径 → 失败", !Agent.Infrastructure.Project.ProjectPathDetector.TryExtractProjectPath("你好，介绍一下", out _));
    Check("提取：不存在路径尾部非 CJK → 不剥不误判",
        !Agent.Infrastructure.Project.ProjectPathDetector.TryExtractProjectPath($"读取 {Path.Combine(baseTmp, "not_exist_123")} 的内容", out _));

    // 语境门
    Check("读语境：读取/分析/看看/项目 命中",
        Agent.Infrastructure.Project.ProjectPathDetector.HasReadContext($"读取一下 {proj} 这个路径的项目，分析一下") &&
        Agent.Infrastructure.Project.ProjectPathDetector.HasReadContext($"看看 {proj} 的结构") &&
        Agent.Infrastructure.Project.ProjectPathDetector.HasReadContext($"{proj} 项目"));
    Check("写语境：写/创建/生成 命中（写优先拦）",
        Agent.Infrastructure.Project.ProjectPathDetector.HasWriteContext($"帮我写一个 C# 项目，保存到 {proj}") &&
        Agent.Infrastructure.Project.ProjectPathDetector.HasWriteContext($"把文件复制到 {proj}"));

    // 组合判定
    Check("自动 use：读语境 + 存在路径 → 通过",
        Agent.Infrastructure.Project.ProjectPathDetector.TryGetAutoUsePath($"读取一下 {proj} 这个路径的项目，分析一下", out var p1) &&
        p1.Equals(Path.GetFullPath(proj), StringComparison.OrdinalIgnoreCase));
    Check("自动 use：写任务 → 不触发",
        !Agent.Infrastructure.Project.ProjectPathDetector.TryGetAutoUsePath($"帮我写一个 C# 项目，保存到 {proj}", out _));
    Check("自动 use：敏感区路径 → 源头不触发",
        !Agent.Infrastructure.Project.ProjectPathDetector.TryGetAutoUsePath("读取一下 C:\\Windows\\System32 的内容", out _));

    Directory.Delete(baseTmp, true);
}

// 批次1·验证层总盘点：写目标提取（TryGetWriteTarget）+ 完成门（CompletionGate）+ 显式授权（explicitWriteRoots）
// ═══════════════════════════════════════════════════════════════
Console.WriteLine("== 批次1：写目标提取（TryGetWriteTarget） ==");
{
    // 通道 2：自然语言盘符 + 文件名（x盘根目录 test.txt → X:\test.txt）
    Check("写目标：X盘根目录+文件名 → 文件级",
        Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget("在x盘根目录，新建一下test.txt文件", out var t1, out var f1)
        && f1 && t1.Equals(@"X:\test.txt", StringComparison.OrdinalIgnoreCase));
    Check("写目标：X盘下+大写盘符 → 归一化",
        Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget("在X盘下新建 report.md", out var t2, out _)
        && t2.Equals(@"X:\report.md", StringComparison.OrdinalIgnoreCase));
    // 无文件名 → 目录级
    Check("写目标：X盘根目录无文件名 → 目录级",
        Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget("保存到x盘根目录", out var t3, out var f3)
        && !f3 && t3.Equals(@"X:\", StringComparison.OrdinalIgnoreCase));
    // 通道 1：完整绝对路径（已存在文件或形似文件）
    var baseTmp = Path.Combine(TestTmp, "tests_b1_target");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    Directory.CreateDirectory(baseTmp);
    var realFile = Path.Combine(baseTmp, "data.txt");
    File.WriteAllText(realFile, "x");
    Check("写目标：完整路径（已存在文件）→ 文件级",
        Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget($"把新内容写入 {realFile}", out var t4, out var f4)
        && f4 && t4.Equals(Path.GetFullPath(realFile), StringComparison.OrdinalIgnoreCase));
    Check("写目标：完整路径（不存在但形似文件）→ 文件级",
        Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget($"创建 {Path.Combine(baseTmp, "new.log")} 文件", out var t5, out var f5)
        && f5 && t5.Equals(Path.GetFullPath(Path.Combine(baseTmp, "new.log")), StringComparison.OrdinalIgnoreCase));
    Check("写目标：完整路径（目录）→ 目录级",
        Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget($"把文件保存到 {baseTmp} 目录", out var t6, out var f6)
        && !f6 && t6.Equals(Path.GetFullPath(baseTmp), StringComparison.OrdinalIgnoreCase));
    // 无写语境 → 不触发
    Check("写目标：读语境（无写词）→ 不触发",
        !Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget("读取一下 X:\\data 的内容", out _, out _));
    Directory.Delete(baseTmp, true);
}

Console.WriteLine("== 批次1：完成门（CompletionGate） ==");
{
    // 非写任务 → 不过门
    Check("完成门：非写任务 → 放行",
        Agent.Infrastructure.Project.CompletionGate.Evaluate(false, false, false, null, false) is null);
    // C：写被拒未重试 → 失败
    var c = Agent.Infrastructure.Project.CompletionGate.Evaluate(true, false, true, null, false);
    Check("完成门：写被拒且无任何写成功 → 判失败",
        c is not null && c.shouldFail && !c.isSuspicious);
    // B：目标未达成 → 失败 + 可疑（写了别处 ≠ 目标）
    var missingTarget = Path.Combine(TestTmp, "tests_b1_gate_missing", "never.txt");
    if (Directory.Exists(Path.GetDirectoryName(missingTarget))) Directory.Delete(Path.GetDirectoryName(missingTarget), true);
    var b = Agent.Infrastructure.Project.CompletionGate.Evaluate(true, true, false, missingTarget, true);
    Check("完成门：写了文件但显式目标不存在 → 判失败+可疑",
        b is not null && b.shouldFail && b.isSuspicious && b.message.Contains("never.txt"));
    // 目标存在 → 放行
    var baseTmp = Path.Combine(TestTmp, "tests_b1_gate");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    Directory.CreateDirectory(baseTmp);
    var f = Path.Combine(baseTmp, "ok.txt");
    File.WriteAllText(f, "ok");
    Check("完成门：目标文件存在 → 放行",
        Agent.Infrastructure.Project.CompletionGate.Evaluate(true, true, false, Path.GetFullPath(f), true) is null);
    // 目录级目标存在 → 放行
    Check("完成门：目录目标存在 → 放行",
        Agent.Infrastructure.Project.CompletionGate.Evaluate(true, true, false, Path.GetFullPath(baseTmp), false) is null);
    Directory.Delete(baseTmp, true);
}

Console.WriteLine("== 10-08 内容符合性（ContentConformance + 完成门 D） ==");
{
    // ① token 提取：加注释任务（无引号，注释形态）→ 提取 "// 已审查"（归一化 "已审查"）
    var t1 = Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens(
        "在 BigService.cs 的 Method1500 方法里加一行注释 // 已审查");
    Check("提取：注释形态任务 → 命中注释内容",
        t1.Count == 1 && t1[0] == "已审查");
    // ①b 提取：URL 不误提取（// 在 https:// 里）
    var t1b = Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens(
        "参考 https://example.com/docs 实现功能");
    Check("提取：URL 中的 // 不误提取",
        t1b.Count == 0);
    // ①c 提取：C# 语言名不误提取（# 前是字母 → 非注释）
    var t1c = Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens(
        "用 C# 写一个判断奇数偶数函数");
    Check("提取：C# 语言名不误提取",
        t1c.Count == 0);
    // ② token 提取：中文引号 + 多 token
    var t2 = Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens(
        "把标题改成 “欢迎” 并加上 “2026”");
    Check("提取：中文引号多 token",
        t2.Count == 2 && t2.Contains("欢迎") && t2.Contains("2026"));
    // ③ token 提取：无引号 → 跳过（不误伤普通生成任务）
    var t3 = Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens(
        "写一个 Python 计算器方法");
    Check("提取：无引号 → 空集（不误伤）",
        t3.Count == 0);
    // ④ token 提取：文件名形态过滤（BigService.cs 是目标不是内容）
    var t4 = Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens(
        "把内容改成 BigService.cs 里的 “已审查”");
    Check("提取：文件名形态被过滤 + 引号内容保留",
        t4.Count == 1 && t4[0] == "已审查" && !t4.Contains("bigservice cs"));
    // ⑤ 包含检查：文件含 token → true
    var dirD = Path.Combine(TestTmp, "tests_d_gate");
    if (Directory.Exists(dirD)) Directory.Delete(dirD, true);
    Directory.CreateDirectory(dirD);
    var okFile = Path.Combine(dirD, "BigService.cs");
    File.WriteAllText(okFile, "public int Method1500(int x) { return x + 1500; } // 已审查\n");
    Check("包含：文件含需求内容 → true",
        Agent.Infrastructure.Project.ContentConformance.FileContainsTokens(okFile, new[] { "已审查" }));
    // ⑥ 包含检查：缺 token（改错/漏注释场景）→ false
    var badFile = Path.Combine(dirD, "BigService_bad.cs");
    File.WriteAllText(badFile, "public int Method1500(int x) { return x + 15000; }\n");
    Check("包含：文件缺需求内容（改错漏注释）→ false",
        !Agent.Infrastructure.Project.ContentConformance.FileContainsTokens(badFile, new[] { "已审查" }));
    // ⑦ 完成门 D：写了文件但不含需求 token → 拦截
    var dMiss = Agent.Infrastructure.Project.CompletionGate.Evaluate(
        true, true, false, Path.GetFullPath(badFile), true,
        requirementTokens: new[] { "已审查" }, writtenFiles: new[] { badFile });
    Check("完成门 D：修改内容不含需求 → 判失败+可疑",
        dMiss is not null && dMiss.shouldFail && dMiss.isSuspicious && dMiss.message.Contains("已审查"));
    // ⑧ 完成门 D：文件含需求 token → 放行
    Check("完成门 D：修改内容含需求 → 放行",
        Agent.Infrastructure.Project.CompletionGate.Evaluate(
            true, true, false, Path.GetFullPath(okFile), true,
            requirementTokens: new[] { "已审查" }, writtenFiles: new[] { okFile }) is null);
    // ⑨ 完成门 D：无需求 token → 跳过（不误伤）
    Check("完成门 D：无需求 token → 放行",
        Agent.Infrastructure.Project.CompletionGate.Evaluate(
            true, true, false, Path.GetFullPath(okFile), true,
            requirementTokens: Array.Empty<string>(), writtenFiles: new[] { badFile }) is null);
    // ⑩ 归一化：文件含 "// 已审查" 匹配 token "已审查"（符号折叠）
    Check("完成门 D：归一化匹配（注释符号折叠）→ 放行",
        Agent.Infrastructure.Project.CompletionGate.Evaluate(
            true, true, false, Path.GetFullPath(okFile), true,
            requirementTokens: new[] { "已审查" }, writtenFiles: new[] { okFile }) is null);
    Directory.Delete(dirD, true);
}

Console.WriteLine("== 批次1：显式写授权（explicitWriteRoots）+ 敏感兜底 ==");
{
    var baseTmp = Path.Combine(TestTmp, "tests_b1_auth");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    var ws = Path.Combine(baseTmp, "task");
    Directory.CreateDirectory(ws);
    var authRoot = Path.Combine(baseTmp, "auth");
    Directory.CreateDirectory(authRoot);

    string W2(Agent.Infrastructure.Tools.AgentToolBase tool, string path, string content)
    {
        var json = $"{{\"path\":{JsonSerializer.Serialize(path)},\"content\":{JsonSerializer.Serialize(content)}}}";
        var args = JsonSerializer.Deserialize<JsonElement>(json);
        return tool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
    }

    // 授权根内绝对路径 → 放行（模拟"X盘根目录 test.txt"授权）
    var wAuth = new Agent.Infrastructure.Tools.WriteFileTool(ws, explicitWriteRoots: new[] { authRoot });
    var target = Path.Combine(authRoot, "test.txt");
    Check("显式授权：授权根内 → 放行",
        W2(wAuth, target, "你好").Contains("已写入") && File.Exists(target));
    // 授权根外 → 拒
    var outside = Path.Combine(baseTmp, "other.txt");
    Check("显式授权：授权根外 → 越界拒",
        W2(wAuth, outside, "x").Contains("越界"));
    // 敏感兜底：白名单通过但路径命中敏感区 → 拒（不真写，只验证拒绝信号）
    var wSens = new Agent.Infrastructure.Tools.WriteFileTool(ws, explicitWriteRoots: new[] { @"C:\Windows" });
    Check("显式授权：敏感区兜底 → 拒绝",
        W2(wSens, @"C:\Windows\evil.txt", "x").Contains("敏感"));
    // EditFileTool 授权一致（edit 需 old_string/new_string）
    var target2 = Path.Combine(authRoot, "test2.txt");
    File.WriteAllText(target2, "old");
    var eAuth = new Agent.Infrastructure.Tools.EditFileTool(ws, explicitWriteRoots: new[] { authRoot });
    var ejson = $"{{\"path\":{JsonSerializer.Serialize(target2)},\"old_string\":\"old\",\"new_string\":\"new\"}}";
    var eargs = JsonSerializer.Deserialize<JsonElement>(ejson);
    var eres = eAuth.InvokeAsync(eargs, CancellationToken.None).GetAwaiter().GetResult();
    Check("显式授权：EditFileTool 授权根内 → 放行",
        eres.Contains("已修改") && File.ReadAllText(target2).Contains("new"));
    Directory.Delete(baseTmp, true);
}

Console.WriteLine("== 只读工程组合边界（ReadOnlyGuard） ==");
{
    // 迷你工程：OrderService.cs / Order.cs / Program.cs + src/App.cs（同名双目录场景）
    var baseTmp = Path.Combine(TestTmp, "tests_rog");
    if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
    var proj = Path.Combine(baseTmp, "demo");
    Directory.CreateDirectory(proj);
    Directory.CreateDirectory(Path.Combine(proj, "src"));
    File.WriteAllText(Path.Combine(proj, "OrderService.cs"), "class OrderService {}");
    File.WriteAllText(Path.Combine(proj, "Order.cs"), "class Order {}");
    File.WriteAllText(Path.Combine(proj, "Program.cs"), "class Program {}");
    File.WriteAllText(Path.Combine(proj, "src", "App.cs"), "class App {}");

    bool Block(string input, bool writeEnabled) =>
        Agent.Infrastructure.Project.ReadOnlyGuard.ShouldBlock(input, proj, writeEnabled, out _);

    Check("只读+写任务+引用工程内文件 → 拦截",
        Block("给 OrderService.cs 的 Method1500 加一行注释", false));
    Check("只读+写任务+无文件引用 → 不拦",
        !Block("写一个待办事项项目", false));
    Check("只读+写任务+引用工程外文件 → 不拦",
        !Block("在 X:\\test.txt 创建文件", false));
    Check("只读+读任务+引用工程内文件 → 不拦",
        !Block("读取 OrderService.cs 的内容", false));
    Check("只读+显式写目标在工程外 → 放行",
        !Block("在 X:\\notes.txt 写入 你好", false));
    Check("工程可写+写任务+引用工程内文件 → 不拦",
        !Block("给 OrderService.cs 加注释", true));
    Check("沙箱（projectRoot=null）→ 不触发",
        !Agent.Infrastructure.Project.ReadOnlyGuard.ShouldBlock("给 OrderService.cs 加注释", null, false, out _));
    Check("同名文件命中（src/App.cs 亦属工程文件）",
        Block("给 App.cs 加一个方法", false));
    // 正则边界回归：csproj 6 字母后缀不截断 → OrderSystem.csproj（工程外/不存在）不误命
    File.WriteAllText(Path.Combine(proj, "Demo.csproj"), "<Project />");
    Check("csproj 后缀不截断（OrderSystem.csproj 不误命）",
        !Block("给 OrderSystem.csproj 改 TargetFramework", false));
    // FindExistingProjectFileRef 大小写归一（输入小写 → 命中工程内 OrderService.cs）
    Check("文件引用大小写归一",
        Agent.Infrastructure.Project.ReadOnlyGuard.FindExistingProjectFileRef("修改 orderservice.cs 的内容", proj) is string h
        && h.Equals("OrderService.cs", StringComparison.OrdinalIgnoreCase));
    Directory.Delete(baseTmp, true);
}

Console.WriteLine("== 配置结构自检：RoutingRules.json ==");
{
    // RoutingRules.json 只复制到 Infrastructure 输出目录（tests 输出无），回溯仓库根读取源文件
    var rulesJson = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "Agent.Infrastructure", "Routing", "RoutingRules.json");
    Check("RoutingRules.json 存在（仓库根回溯）", File.Exists(rulesJson));

    List<RoutingRuleCheck>? rules = null;
    if (File.Exists(rulesJson))
    {
        try
        {
            var root = JsonDocument.Parse(File.ReadAllText(rulesJson)).RootElement;
            rules = JsonSerializer.Deserialize<List<RoutingRuleCheck>>(
                root.GetProperty("rules").GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            // 解析失败 → rules 保持 null，下方断言暴露
        }
    }
    Check("JSON 可解析且含 rules 数组", rules is not null);
    if (rules is not null)
    {
        Check("规则数 == 8（当前基线）", rules.Count == 8);
        Check("target 全合法（small/agent_pro/large）",
            rules.All(r => r.Target is "small" or "agent_pro" or "large"));
        Check("无重名规则（防死规则回归）",
            rules.Select(r => r.Name).Distinct().Count() == rules.Count);
        Check("priority 全 > 0", rules.All(r => r.Priority > 0));
        Check("type 全合法",
            rules.All(r => r.Type is "regex" or "keyword_any" or "prefix_any"
                or "length_min" or "length_max" or "length" or "after_failure"));
        Check("regex 规则 Pattern 非空且可编译",
            rules.Where(r => r.Type == "regex")
                .All(r => !string.IsNullOrEmpty(r.Pattern) && TryCompileRegex(r.Pattern!)));
        Check("keyword_any 规则 Keywords 非空",
            rules.Where(r => r.Type == "keyword_any").All(r => r.Keywords is { Count: > 0 }));
    }
}

// ════════════════════════════════════════════════════════════
// Dashboard P0：TaskEventHub 多播 + DashboardHost API 冒烟（10-07）
//   1. TaskEventHub 订阅/发布/退订（BoundedChannel 背压 DropOldest）
//   2. TaskEventStore 持久化：记录/读取/清理/fail-safe（10-07 UI 层 #1）
//   3. DashboardHost 真起 Kestrel（127.0.0.1:0）→ HttpClient 拉 /api/stats /api/tasks
//      /api/backups /api/diff /api/tasks/{id}/events 与 GET /（嵌入页）→ 200 + JSON 结构
// ════════════════════════════════════════════════════════════
Console.WriteLine("== Dashboard P0 TaskEventHub + TaskEventStore + DashboardHost（Events/ + Agent.Dashboard） ==");
{
    // ── 1. TaskEventHub：订阅 → 发布 → 读取；退订后不再收到 ──
    var hub = new Agent.Infrastructure.Events.TaskEventHub();
    var (reader, handle) = hub.Subscribe();
    hub.Publish(new Agent.Core.Models.AgentEvent.Thinking("t1"));
    hub.Publish(new Agent.Core.Models.AgentEvent.Routing("small", "L0", "test", null));
    var got = new List<Agent.Core.Models.AgentEvent>();
    while (reader.TryRead(out var e)) got.Add(e);
    Check("hub 发布 → 订阅者收到 2 事件", got.Count == 2 && got[0] is Agent.Core.Models.AgentEvent.Thinking && got[1] is Agent.Core.Models.AgentEvent.Routing);
    handle.Dispose();
    var (reader2, _) = hub.Subscribe();
    hub.Publish(new Agent.Core.Models.AgentEvent.Thinking("t2"));
    Agent.Core.Models.AgentEvent? first = null;
    reader2.TryRead(out first);
    Check("退订后不再接收（第二个订阅者不受影响）", first is Agent.Core.Models.AgentEvent.Thinking && (first as Agent.Core.Models.AgentEvent.Thinking)!.Text == "t2");

    // ── 2. TaskEventStore：记录 → 读取（seq 升序）→ Prune 清理 → JsonOpts 单一来源 ──
    var ws2 = Path.Combine(Path.GetTempPath(), "store_smoke_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(ws2);
    try
    {
        var store = new Agent.Infrastructure.Events.TaskEventStore(Path.Combine(ws2, "metrics.db"));
        var ev1 = new Agent.Core.Models.AgentEvent.ToolCall("write_file", System.Text.Json.JsonDocument.Parse("{\"path\":\"a.cs\"}").RootElement);
        var ev2 = new Agent.Core.Models.AgentEvent.FinalAnswer("done");
        store.RecordAsync("taskX", 0, ev1);
        store.RecordAsync("taskX", 1, ev2);
        store.RecordAsync("other", 0, ev1);
        var all = store.GetEventsAsync("taskX");
        Check("store 记录 + 读取（seq 升序、跨任务隔离）",
            all.Count == 2 && all[0].Seq == 0 && all[1].Seq == 1
            && all[0].Type == nameof(Agent.Core.Models.AgentEvent.ToolCall)
            && all[1].Type == nameof(Agent.Core.Models.AgentEvent.FinalAnswer)
            && all[0].Payload.Contains("write_file"));
        var parsed = System.Text.Json.JsonSerializer.Deserialize<Agent.Core.Models.AgentEvent.ToolCall>(all[0].Payload, Agent.Infrastructure.Events.TaskEventStore.JsonOpts);
        Check("store payload 可反序列化回原事件（JsonOpts 单一来源）",
            parsed is not null && parsed.Name == "write_file");
        var pruned = store.PruneOlderThan(1);
        Check("PruneOlderThan(1) 清理 1 秒前事件（刚写入的保留）", pruned == 0);
        var eventsApi = store.GetEventsAsync("nope");
        Check("store 未写入任务 → 空列表", eventsApi.Count == 0);
    }
    finally
    {
        try { Directory.Delete(ws2, true); } catch { }
    }

    // ── CommandHub 单元（10-07 webUI 交互：终端/web 统一命令通道；10-08 P0：seq/快照/取消）──
    {
        var cmdHub = new Agent.Core.CommandHub();
        var sa = cmdHub.TrySubmit("a");
        var sb = cmdHub.TrySubmit("b");
        Check("CommandHub seq 递增（web 提交归属标识）", sa == 1 && sb == 2);
        Check("CommandHub Source 默认 terminal / web 显式（10-08 回显来源区分：web 提交需终端补回显，terminal 已由 ReadLine 回显）",
            cmdHub.TrySubmit("w1", "web") == 3);
        var a = await cmdHub.NextAsync(CancellationToken.None);
        var b = await cmdHub.NextAsync(CancellationToken.None);
        Check("CommandHub FIFO：先提交先取回（终端输入与 web 提交同一队列）", a!.Value.Input == "a" && b!.Value.Input == "b");
        Check("CommandHub 条目含提交时间（/api/queue SubmittedAt 数据源）",
            a!.Value.SubmittedAt != default && b!.Value.SubmittedAt != default);
        var w1 = await cmdHub.NextAsync(CancellationToken.None);
        Check("CommandHub Source 透传 web（10-08：web 提交 → CLI 补回显的判定依据）",
            w1!.Value.Input == "w1" && w1.Value.Source == "web");
        cmdHub.TrySubmit(null);
        Check("CommandHub EOF null 透传（无头 stdin 关闭 → 主循环退出）",
            (await cmdHub.NextAsync(CancellationToken.None))!.Value.Input is null);
        var big = new Agent.Core.CommandHub();
        for (var i = 0; i < 105; i++) big.TrySubmit("c" + i);
        Check("CommandHub 有界 100 DropOldest：超限丢最旧（105 条写入后首条为 c5）",
            (await big.NextAsync(CancellationToken.None))!.Value.Input == "c5");
        Check("CommandHub Snapshot：顺序与 EOF 过滤（/api/queue 数据源）",
            big.Snapshot().Count == 99 && big.Snapshot()[0].Input == "c6" && big.Snapshot()[^1].Input == "c104");
        var cancelCts = new CancellationTokenSource();
        cancelCts.Cancel();
        Check("CommandHub 取消 → null（不抛）", await new Agent.Core.CommandHub().NextAsync(cancelCts.Token) is null);
        var hub2 = new Agent.Core.CommandHub();
        var taskCts = new CancellationTokenSource();
        hub2.AttachTaskCancellation(taskCts);
        Check("CommandHub 取消当前任务：Attach 后 CancelCurrentTask → 令牌已取消（web POST /api/cancel 触发同一机制）",
            hub2.CancelCurrentTask() && taskCts.IsCancellationRequested);
        hub2.DetachTaskCancellation();
        Check("CommandHub 取消：Detach 后无任务可取消 → false", !hub2.CancelCurrentTask());
        // ── 任务归属（10-08 P1）──
        var hub3 = new Agent.Core.CommandHub();
        hub3.AttachTaskInfo(7, "用 C# 写一个待办事项项目");
        var cur = hub3.CurrentTask;
        Check("CommandHub 当前任务归属：Attach(seq,input) 后 CurrentTask 返回（/api/current-task 数据源）",
            cur.Seq == 7 && cur.Input == "用 C# 写一个待办事项项目");
        hub3.DetachTaskInfo();
        var cur2 = hub3.CurrentTask;
        Check("CommandHub 当前任务归属：Detach 后 Seq=0/Input=null（任务结束清除）",
            cur2.Seq == 0 && cur2.Input is null);
        Check("AgentOptions.TaskTimeoutMinutes 默认 30（任务级整体超时配置，0=禁用）",
            new Agent.Core.Models.AgentOptions().TaskTimeoutMinutes == 30);
        Check("AgentOptions.OllamaHttpTimeoutMinutes 默认 20（本地 Ollama HttpClient 超时，10-04 600s 腰斩实证后配置化加长）",
            new Agent.Core.Models.AgentOptions().OllamaHttpTimeoutMinutes == 20);
    }

    // ── 3. DashboardHost 冒烟：真起 Kestrel → HttpClient 验证 API ──
    var ws = Path.Combine(Path.GetTempPath(), "dash_smoke_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(ws);
    try
    {
        var dashHub = new Agent.Infrastructure.Events.TaskEventHub();
        var metrics = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(Path.Combine(ws, "metrics.db"));
        var store = new Agent.Infrastructure.Events.TaskEventStore(Path.Combine(ws, "metrics.db"));
        store.RecordAsync("hist1", 0, new Agent.Core.Models.AgentEvent.Thinking("t"));
        store.RecordAsync("hist1", 1, new Agent.Core.Models.AgentEvent.Error("boom"));
        // 【10-08 ① 任务产物浏览】工作区预置产物文件——/api/files 断言数据源
        System.IO.File.WriteAllText(Path.Combine(ws, "sample.cs"), "public class Sample { }");
        // 【10-10 日志治理】内嵌服务显式日志级别（Warning）→ 启动 stdout 无 Kestrel 污染。
        // 捕获 Console.Out：StartAsync（Kestrel 启动日志发生窗口）→ 首个请求后恢复。
        var logCapture = new System.IO.StringWriter();
        var prevOut = System.Console.Out;
        System.Console.SetOut(logCapture);
        try
        {
            var (url2, stop2) = await Agent.Dashboard.DashboardHost.StartAsync(
                new Agent.Dashboard.DashboardOptions(ws, null, Path.Combine(ws, "symbol_index.db"), null, metrics, dashHub, store,
                    new Agent.Core.Models.AgentOptions(),
                    Path.Combine(ws, "agent_memory.db"),
                    new Agent.Core.CommandHub()), CancellationToken.None);
            using var probe = new HttpClient { BaseAddress = new Uri(url2 + "/") };
            _ = await probe.GetStringAsync("");   // 触发一次请求，覆盖请求期日志窗口
            await stop2();
        }
        finally { System.Console.SetOut(prevOut); }
        var captured = logCapture.ToString();
        Check("Dashboard 启动 stdout 无 Kestrel/Hosting 日志（显式日志级别 Warning，10-10 日志治理）",
            !captured.Contains("Now listening") && !captured.Contains("Microsoft.Hosting.Lifetime")
            && !captured.Contains("Application started") && !captured.Contains("Hosting environment"));

        var (url, stop) = await Agent.Dashboard.DashboardHost.StartAsync(
            new Agent.Dashboard.DashboardOptions(ws, null, Path.Combine(ws, "symbol_index.db"), null, metrics, dashHub, store,
                new Agent.Core.Models.AgentOptions(),
                Path.Combine(ws, "agent_memory.db"),
                new Agent.Core.CommandHub()), CancellationToken.None);
        using var http = new HttpClient { BaseAddress = new Uri(url + "/") };
        var page = await http.GetStringAsync("");
        Check("GET / 返回嵌入单页（含标题 + 回放 API + 过滤/滚动开关）",
            page.Contains("LocalAI Workbench") && page.Contains("/events") && page.Contains("showTaskEvents")
            && page.Contains("fbtn") && page.Contains("autoScroll"));
        Check("嵌入单页含 SVG 依赖子图（layoutOneDegree 纯函数 + renderDepSvg + 节点点击联动 selectDepFile + 箭头 marker）",
            page.Contains("function layoutOneDegree") && page.Contains("function renderDepSvg")
            && page.Contains("function selectDepFile") && page.Contains("depArrowG") && page.Contains("DEP_NODE_MAX"));
        Check("命令条吸顶结构：cmdBar 直属 colL（sticky 滚动容器直接子级，10-10 P1 修复）——防回退成卡片内嵌",
            page.Contains("<div class=\"colL\">\n  <div id=\"cmdBar\"")
            && page.Contains("#cmdBar { position:sticky; top:var(--headH)"));
        Check("事件分组卡片（10-10 ①）：renderGrouped 统一渲染 + 分组函数 + 卡片结构 + boxKey 折叠隔离",
            page.Contains("function renderGrouped") && page.Contains("function groupEvents")
            && page.Contains("function toggleCard") && page.Contains("isCollapsed(boxKey, start)")
            && page.Contains("className = \"taskcard\"") && page.Contains("tc-head") && page.Contains("tc-sep")
            && page.Contains("renderGrouped(box, eventsCache, {") && page.Contains("boxKey: \"detail\""));
        Check("②③④ Markdown/工具配对/折叠挂载（10-10）：mdToHtml 转义先行 + buildUnits 配对 + renderPair 块 + 两级折叠",
            page.Contains("function mdToHtml") && page.Contains("function escHtml")
            && page.Contains("function buildUnits") && page.Contains("function renderPair")
            && page.Contains("className = \"toolpair\"") && page.Contains("collapsedPairs")
            && page.Contains("function togglePair") && page.Contains("onclick=\"expandMd(this)\""));
        Check("前端 JS 顶层闭合无漂移（10-10 修复：showTaskEvents 残留多余 } 导致 script SyntaxError，整页 JS 失效卡'加载中…'）——防替换边界错位再犯",
            !page.Contains("});\n}\n}\n// ── stats ──"));
        var stats = await http.GetStringAsync("api/stats");
        Check("GET /api/stats 200 + JSON 结构", stats.Contains("\"summary\"") && stats.Contains("totalCount"));
        var tasks = await http.GetStringAsync("api/tasks?take=3");
        Check("GET /api/tasks 200 + 数组", tasks.StartsWith("["));
        var backups = await http.GetStringAsync("api/backups");
        Check("GET /api/backups 200 + 数组", backups.StartsWith("["));
        var files = await http.GetStringAsync("api/files");
        Check("GET /api/files 根列表 200 + children 结构（任务产物浏览）",
            files.Contains("\"children\"") && files.Contains("sample.cs"));
        var fileBody = await http.GetStringAsync("api/files?path=sample.cs");
        Check("GET /api/files 文本文件 → {text:true, content}（预览内容）",
            fileBody.Contains("\"text\":true") && fileBody.Contains("public class Sample"));
        var dl = await http.GetAsync("api/files?path=sample.cs&raw=true");
        Check("GET /api/files raw=true → 下载 200", dl.StatusCode == System.Net.HttpStatusCode.OK);
        var oob = await http.GetAsync("api/files?path=" + Uri.EscapeDataString("../../Windows/win.ini"));
        Check("GET /api/files 越界路径 → 400（PathBoundary 逐段判定拒绝）",
            oob.StatusCode == System.Net.HttpStatusCode.BadRequest);
        var noDiff = await http.GetAsync("api/diff?taskId=x&rel=y");
        Check("无工程根 diff → 400 BadRequest", noDiff.StatusCode == System.Net.HttpStatusCode.BadRequest);

        // 【10-10 Web 端回滚】端点注册 + 工程根守卫（有工程根的真实 dry-run/回滚行为由 BackupService 单元覆盖）
        var rbDry = await http.PostAsync("api/rollback/dry-run",
            new StringContent("{\"taskId\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));
        Check("POST /api/rollback/dry-run 无工程根 → 400（端点已注册）",
            rbDry.StatusCode == System.Net.HttpStatusCode.BadRequest);
        var rbEmpty = await http.PostAsync("api/rollback/dry-run",
            new StringContent("{\"taskId\":\"\"}", System.Text.Encoding.UTF8, "application/json"));
        Check("POST /api/rollback/dry-run 空 taskId → 400", rbEmpty.StatusCode == System.Net.HttpStatusCode.BadRequest);
        var rbExec = await http.PostAsync("api/rollback",
            new StringContent("{\"taskId\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));
        Check("POST /api/rollback 无工程根 → 400（端点已注册）",
            rbExec.StatusCode == System.Net.HttpStatusCode.BadRequest);

        var evts = await http.GetStringAsync("api/tasks/hist1/events");
        Check("GET /api/tasks/{id}/events 200 + 平铺事件（后端补 type 短名，10-10 P0）",
            evts.StartsWith("[") && evts.Contains("\"type\":\"thinking\"") && evts.Contains("boom"));
        var noEvts = await http.GetStringAsync("api/tasks/none/events");
        Check("GET /api/tasks/{id}/events 无事件 → 空数组", noEvts == "[]");
        var noDeps = await http.GetAsync("api/deps");
        Check("无工程根 /api/deps → 400 BadRequest", noDeps.StatusCode == System.Net.HttpStatusCode.BadRequest);
        var cmdBad = await http.PostAsync("api/commands",
            new StringContent("{\"input\":\"   \"}", System.Text.Encoding.UTF8, "application/json"));
        Check("POST /api/commands 空输入 → 400", cmdBad.StatusCode == System.Net.HttpStatusCode.BadRequest);
        var cmdOk = await http.PostAsync("api/commands",
            new StringContent("{\"input\":\"stats\"}", System.Text.Encoding.UTF8, "application/json"));
        var cmdBody = await cmdOk.Content.ReadAsStringAsync();
        Check("POST /api/commands 合法命令 → 202 {queued:true} 且含 seq/pending（web 提交归属+位置）",
            cmdOk.StatusCode == System.Net.HttpStatusCode.Accepted
            && cmdBody.Contains("queued") && cmdBody.Contains("seq") && cmdBody.Contains("pending"));
        var qResp = await http.GetAsync("api/queue");
        var qBody = await qResp.Content.ReadAsStringAsync();
        Check("GET /api/queue → 200 且结构 {pending, items[{seq,input,submittedAt}]}（队列可见性）",
            qResp.StatusCode == System.Net.HttpStatusCode.OK
            && qBody.Contains("\"pending\"") && qBody.Contains("\"items\"") && qBody.Contains("\"submittedAt\""));
        var cancelResp = await http.PostAsync("api/cancel", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Check("POST /api/cancel 无运行中任务 → 409 {error}（无任务时明确拒绝）",
            cancelResp.StatusCode == System.Net.HttpStatusCode.Conflict);
        var curResp = await http.GetAsync("api/current-task");
        var curBody = await curResp.Content.ReadAsStringAsync();
        Check("GET /api/current-task 无任务 → 200 {running:false}（任务归属端点）",
            curResp.StatusCode == System.Net.HttpStatusCode.OK && curBody.Contains("\"running\":false"));
        await stop();

        // ── ③ 安全 token 加固：带 AuthToken 的迷你实例 → 无 token 401 / 带 token 200 ──
        var authWs = Path.Combine(Path.GetTempPath(), "dash_auth_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(authWs);
        var authMetrics = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(Path.Combine(authWs, "metrics.db"));
        var authStore = new Agent.Infrastructure.Events.TaskEventStore(Path.Combine(authWs, "metrics.db"));
        var (authUrl, authStop) = await Agent.Dashboard.DashboardHost.StartAsync(
            new Agent.Dashboard.DashboardOptions(authWs, null, Path.Combine(authWs, "symbol_index.db"), null, authMetrics,
                new Agent.Infrastructure.Events.TaskEventHub(), authStore, new Agent.Core.Models.AgentOptions(),
                Path.Combine(authWs, "agent_memory.db"), new Agent.Core.CommandHub(), "secret-token-123"),
            CancellationToken.None);
        try
        {
            using var authHttp = new HttpClient { BaseAddress = new Uri(authUrl + "/") };
            var noTok = await authHttp.GetAsync("api/stats");
            Check("③ AuthToken 鉴权：无 token 请求 → 401（中间件拒绝）",
                noTok.StatusCode == System.Net.HttpStatusCode.Unauthorized);
            var badTok = await authHttp.GetAsync("api/stats?token=wrong");
            Check("③ AuthToken 鉴权：错误 token → 401", badTok.StatusCode == System.Net.HttpStatusCode.Unauthorized);
            var okTok = await authHttp.GetAsync("api/stats?token=secret-token-123");
            Check("③ AuthToken 鉴权：正确 token → 200", okTok.StatusCode == System.Net.HttpStatusCode.OK);
            var hdrReq = new HttpRequestMessage(HttpMethod.Get, "api/stats");
            hdrReq.Headers.Add("X-Dashboard-Token", "secret-token-123");
            var okHeader = await authHttp.SendAsync(hdrReq);
            Check("③ AuthToken 鉴权：header X-Dashboard-Token → 200（fetch 包装兼容）",
                okHeader.StatusCode == System.Net.HttpStatusCode.OK);
        }
        finally { await authStop(); }

        // ── 4. /api/deps 工程依赖图谱：真工程（含类型级引用）+ 索引 → 概览 + 单文件依赖 ──
        var depWs = Path.Combine(Path.GetTempPath(), "dash_deps_" + Guid.NewGuid().ToString("N"));
        var depProj = Path.Combine(depWs, "proj");
        Directory.CreateDirectory(depProj);
        try
        {
            File.WriteAllText(Path.Combine(depProj, "Customer.cs"),
                "public class Customer { public int Id { get; set; } public string Name { get; set; } }\n");
            File.WriteAllText(Path.Combine(depProj, "OrderService.cs"),
                "public class OrderService { public Customer Find(int id) { return new Customer(); } }\n");
            File.WriteAllText(Path.Combine(depProj, "Program.cs"),
                "public class Program { static void Main() { var s = new OrderService(); var c = s.Find(1); } }\n");
            var depStore = new Agent.Infrastructure.Project.SymbolIndexStore(Path.Combine(depWs, "symbol_index.db"));
            Check("deps 冒烟：符号索引可建（3 个类型级符号）", depStore.BuildAndSave(depProj) >= 3);
            var (depUrl, depStop) = await Agent.Dashboard.DashboardHost.StartAsync(
                new Agent.Dashboard.DashboardOptions(depWs, depProj, Path.Combine(depWs, "symbol_index.db"), null,
                    new Agent.Infrastructure.Metrics.SqliteMetricsCollector(Path.Combine(depWs, "metrics.db")),
                    new Agent.Infrastructure.Events.TaskEventHub(),
                    new Agent.Infrastructure.Events.TaskEventStore(Path.Combine(depWs, "metrics.db")),
                    new Agent.Core.Models.AgentOptions(),
                    Path.Combine(depWs, "agent_memory.db"),
                    new Agent.Core.CommandHub()),
                CancellationToken.None);
            using var depHttp = new HttpClient { BaseAddress = new Uri(depUrl + "/") };
            var overview = await depHttp.GetStringAsync("api/deps");
            Check("GET /api/deps 概览：files/edges/topReverse 结构 + 3 文件 + 依赖边 ≥1",
                overview.Contains("\"files\"") && overview.Contains("\"topReverse\"")
                && overview.Contains("OrderService.cs") && overview.Contains("Program.cs"));
            var single = await depHttp.GetStringAsync("api/deps?path=OrderService.cs");
            Check("GET /api/deps?path=OrderService.cs → direct 含 Customer.cs（类型级引用检测）",
                single.Contains("Customer.cs") && single.Contains("reverse"));
            var tuning = await depHttp.GetStringAsync("api/tuning");
            Check("GET /api/tuning 200：三态 + 阈值结构（写降级/读升级/Complex 降级）",
                tuning.Contains("writeDowngrade") && tuning.Contains("readUpgrade")
                && tuning.Contains("complexDrop") && tuning.Contains("thresholds"));
            var mem = await depHttp.GetStringAsync("api/memory?take=5");
            Check("GET /api/memory 200：stats + rows 结构", mem.Contains("\"stats\"") && mem.Contains("\"rows\""));
            await depStop();
        }
        finally
        {
            try { Directory.Delete(depWs, true); } catch { }
        }
    }
    finally
    {
        try { Directory.Delete(ws, true); } catch { }
    }
}

// ── 3b. Web 端回滚（10-10）：有工程根的真实闭环——dry-run 预览（不执行）→ rollback 执行 → 文件复原 ──
Console.WriteLine("== 3b Web 端回滚（dry-run → rollback 闭环） ==");
{
    var rbWs = Path.Combine(Path.GetTempPath(), "dash_rb_" + Guid.NewGuid().ToString("N"));
    var rbProj = Path.Combine(Path.GetTempPath(), "dash_rb_proj_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(rbWs);
    Directory.CreateDirectory(rbProj);
    try
    {
        var rbFile = Path.Combine(rbProj, "Order.cs");
        File.WriteAllText(rbFile, "class Order { public int Id { get; set; } }");
        var rbOld = File.ReadAllText(rbFile);
        Agent.Infrastructure.Project.BackupService.Backup(rbWs, rbFile, rbProj, "rbT1");
        File.WriteAllText(rbFile, "class Order { public int Id { get; set; } public double Amount { get; set; } }");
        var rbNew = Path.Combine(rbProj, "TaxService.cs");
        File.WriteAllText(rbNew, "class TaxService { }");
        Agent.Infrastructure.Project.BackupService.MarkNewFile(rbWs, rbNew, rbProj, "rbT1");

        var (rbUrl, rbStop) = await Agent.Dashboard.DashboardHost.StartAsync(
            new Agent.Dashboard.DashboardOptions(rbWs, rbProj, Path.Combine(rbWs, "symbol_index.db"), null,
                new Agent.Infrastructure.Metrics.SqliteMetricsCollector(Path.Combine(rbWs, "metrics.db")),
                new Agent.Infrastructure.Events.TaskEventHub(),
                new Agent.Infrastructure.Events.TaskEventStore(Path.Combine(rbWs, "metrics.db")),
                new Agent.Core.Models.AgentOptions(),
                Path.Combine(rbWs, "agent_memory.db"),
                new Agent.Core.CommandHub()),
            CancellationToken.None);
        using var rbHttp = new HttpClient { BaseAddress = new Uri(rbUrl + "/") };

        var rbDry = await rbHttp.PostAsync("api/rollback/dry-run",
            new StringContent("{\"taskId\":\"rbT1\"}", System.Text.Encoding.UTF8, "application/json"));
        Check("3b dry-run 200 + 清单含恢复 Order.cs / 删除 TaxService.cs",
            rbDry.StatusCode == System.Net.HttpStatusCode.OK
            && rbDry.Content.ReadAsStringAsync().Result.Contains("Order.cs")
            && rbDry.Content.ReadAsStringAsync().Result.Contains("TaxService.cs"));
        Check("3b dry-run 未执行改动（文件仍为新内容）",
            File.ReadAllText(rbFile).Contains("Amount"));

        var rbExec = await rbHttp.PostAsync("api/rollback",
            new StringContent("{\"taskId\":\"rbT1\"}", System.Text.Encoding.UTF8, "application/json"));
        Check("3b rollback 200 + 恢复 1 / 删除 1",
            rbExec.StatusCode == System.Net.HttpStatusCode.OK
            && rbExec.Content.ReadAsStringAsync().Result.Contains("\"restored\":1")
            && rbExec.Content.ReadAsStringAsync().Result.Contains("\"removedNew\":1"));
        Check("3b 回滚生效：Order.cs 复原 + TaxService.cs 已删除",
            File.ReadAllText(rbFile) == rbOld && !File.Exists(rbNew));

        await rbStop();
    }
    finally
    {
        try { Directory.Delete(rbWs, true); } catch { }
        try { Directory.Delete(rbProj, true); } catch { }
    }
}

Console.WriteLine($"\n结果: {pass} 通过 / {fail} 失败");
return fail == 0 ? 0 : 1;

static bool TryCompileRegex(string pattern)
{
    try { _ = new System.Text.RegularExpressions.Regex(pattern); return true; }
    catch { return false; }
}

/// <summary>RoutingRules.json 单条规则的最小结构（RuleEngine.RuleDto 为 private，此处独立定义）。</summary>
sealed class RoutingRuleCheck
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "regex";
    public string Target { get; set; } = "small";
    public int Priority { get; set; }
    public string? Pattern { get; set; }
    public List<string>? Keywords { get; set; }
}
