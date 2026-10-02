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

Console.WriteLine("== 记忆治理收尾：自动过期清理 + 默认阈值 ==");
{
    // AgentOptions 默认治理阈值（任务启动时自动执行）
    var opt = new Agent.Core.Models.AgentOptions();
    Check("治理默认 30 天", opt.MemoryPruneDays == 30);
    Check("治理默认 1 命中（仅 0 命中可清）", opt.MemoryPruneMinHits == 1);

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

    // 任务级回滚（rollback all 语义）
    var restored = Agent.Infrastructure.Project.BackupService.RollbackTask(work, proj);
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

    Directory.Delete(proj, true);
    Directory.Delete(work, true);
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

Console.WriteLine($"\n结果: {pass} 通过 / {fail} 失败");
return fail == 0 ? 0 : 1;
