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
var TestTmp = Path.Combine(@"X:\.NET AI\LocalAiWorkbench\workspace\test-tmp", Environment.ProcessId.ToString());
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
    var restored = Agent.Infrastructure.Project.BackupService.Rollback(ws, "src/A.cs", proj);
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
    var restoredB = Agent.Infrastructure.Project.BackupService.Rollback(ws2, "src/B.cs", proj);
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
    Check("多条失败 → 计数强化", built2 is not null && built2.Contains("同类失败已记录 2 次"));

    // 只有参考 → 无警示组
    var built3 = Agent.Infrastructure.Memory.MemoryPromptBuilder.Build(new List<Agent.Infrastructure.Memory.AgentMemoryStore.MemoryHit> { okHit });
    Check("仅参考无警示组", built3 is not null && !built3.Contains("历史失败教训") && built3.Contains("历史参考"));
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

    Check("工程模式：盘符路径越界（C:\\Windows）→ 拒绝",
        Rc(proj, "dir C:\\Windows").Contains("越界"));
    Check("工程模式：路径穿越（..）→ 拒绝",
        Rc(proj, "type ..\\..\\Windows\\win.ini").Contains("越界"));
    Check("工程模式：工程根内绝对路径 → 放行",
        !Rc(proj, "dir \"" + proj.Replace("\\", "/") + "\"").Contains("越界"));
    Check("工程模式：相对路径命令 → 放行",
        !Rc(proj, "python main.py").Contains("越界"));
    Check("沙箱模式：不启用边界检查（现状保持）",
        !Rc(null, "dir C:\\Windows").Contains("越界"));
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
    Check("列出目录 → 非写任务",
        !Wr("列出 src/Agent.Core/Models 目录"));
    Check("操作词但无文件语境 → 非写任务",
        !Wr("帮我添加一些建议"));
}
Console.WriteLine($"\n结果: {pass} 通过 / {fail} 失败");
return fail == 0 ? 0 : 1;
