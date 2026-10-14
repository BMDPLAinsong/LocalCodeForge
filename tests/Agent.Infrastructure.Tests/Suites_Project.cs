// ============================================================
// Suites_Project.cs
// 工程侧校验与工具行为（计划冲突 / 完成门检测 / 编辑冲突诊断 / 只读模式 / 测试命令）
// 区段：1411 / 1449 / 1486 / 1516 / 1551
// 依赖：using static AgentTests.TestHarness（Check / TestTmp）
// ============================================================
using System.Text.Json;
using static AgentTests.TestHarness;

namespace AgentTests;

public static class Suites_Project
{
    // ── 区段 1411：工程增量计划校验（CheckIncrementalPlanConflicts） ──
    public static void Run1411()
    {
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
    }

    // ── 区段 1449：FileGrammar（语法单源）+ P-D 索引查询路径 ──
    // 【10-14 P1-5】原 TestProjectDetector 区段：其 HasTests 经全 src 检索确认生产零调用（死代码），
    // 类已删除；原"测试门"语义由 TestRunnerService.DetectTest 唯一承接（见区段 1551）。
    // 【10-14 P1-4/P1-6】本区段改写为 FileGrammar 单源断言 + P-D 索引查询路径断言。
    public static void Run1449()
    {
        Console.WriteLine("== FileGrammar（语法单源）+ P-D 索引查询路径 ==");
        {
            // ── IsExcludedRel：段级精确匹配（任意层级）──
            Check("FileGrammar：顶层 obj/bin/.git 命中排除",
                Agent.Infrastructure.Project.FileGrammar.IsExcludedRel("obj\\Debug\\net10.0\\x.cs")
                && Agent.Infrastructure.Project.FileGrammar.IsExcludedRel("bin\\x.dll")
                && Agent.Infrastructure.Project.FileGrammar.IsExcludedRel("src\\.git\\hooks\\pre-commit"));
            Check("FileGrammar：嵌套 obj/node_modules 命中（原顶层 StartsWith 复制的盲区）",
                Agent.Infrastructure.Project.FileGrammar.IsExcludedRel("src\\obj\\Generated.cs")
                && Agent.Infrastructure.Project.FileGrammar.IsExcludedRel("web\\node_modules\\a.js"));
            Check("FileGrammar：'binary' 前缀不误伤 + 非排除路径放行",
                !Agent.Infrastructure.Project.FileGrammar.IsExcludedRel("binary\\z.cs")
                && !Agent.Infrastructure.Project.FileGrammar.IsExcludedRel("src\\App.cs")
                && !Agent.Infrastructure.Project.FileGrammar.IsExcludedRel(""));

            // ── EnumerateCodeFiles：排除 + 扩展过滤（惰性枚举；IO 异常容忍）──
            var fgDir = Path.Combine(TestTmp, "fg_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(fgDir, "src"));
            Directory.CreateDirectory(Path.Combine(fgDir, "obj"));
            Directory.CreateDirectory(Path.Combine(fgDir, "node_modules"));
            File.WriteAllText(Path.Combine(fgDir, "src", "A.cs"), "class A {}");
            File.WriteAllText(Path.Combine(fgDir, "src", "B.py"), "class B: pass");
            File.WriteAllText(Path.Combine(fgDir, "obj", "Skip.cs"), "class Skip {}");
            File.WriteAllText(Path.Combine(fgDir, "node_modules", "C.js"), "var x = 1;");
            File.WriteAllText(Path.Combine(fgDir, "readme.md"), "# x");
            var fgFiles = Agent.Infrastructure.Project.FileGrammar.EnumerateCodeFiles(fgDir)
                .Select(f => f.Rel.Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            Check("FileGrammar：枚举仅含未排除代码文件（obj/node_modules/readme 排除）",
                fgFiles.Count == 2 && fgFiles.Contains("src/A.cs") && fgFiles.Contains("src/B.py"));
            Check("FileGrammar：空根/不存在根 → 空枚举",
                !Agent.Infrastructure.Project.FileGrammar.EnumerateCodeFiles("").Any()
                && !Agent.Infrastructure.Project.FileGrammar.EnumerateCodeFiles(Path.Combine(fgDir, "nope")).Any());

            // ── 盘符两语法归位（语义不混：中文盘 vs 字面盘）──
            Check("FileGrammar：TryExtractChineseDiskRoot（'保存到D盘根目录' / '放到x盘'）",
                Agent.Infrastructure.Project.FileGrammar.TryExtractChineseDiskRoot("保存到D盘根目录", out var dRoot) && dRoot == "D:\\"
                && Agent.Infrastructure.Project.FileGrammar.TryExtractChineseDiskRoot("放到x盘", out var xRoot) && xRoot == "X:\\");
            Check("FileGrammar：TryExtractLiteralDiskRoot（字面 C:\\ + 存在性校验；中文盘形态不命中）",
                Agent.Infrastructure.Project.FileGrammar.TryExtractLiteralDiskRoot("use --write C:\\logs", out var cRoot) && cRoot == "C:\\"
                && !Agent.Infrastructure.Project.FileGrammar.TryExtractLiteralDiskRoot("放到D盘根目录", out _));

            // ── P-D 索引查询路径（10-14 P1-6）：查符号索引 + 精确复核 + 回退全扫 ──
            var pdIdx = Path.Combine(TestTmp, "pdi_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(pdIdx);
            File.WriteAllText(Path.Combine(pdIdx, "Order.cs"), "public class Order { }\n");
            File.WriteAllText(Path.Combine(pdIdx, "OrderService.cs"), "public class OrderService { }\n");
            // 字符串型幻影：多行字符串里独立成行的 "class X" 会被符号索引收录（其正则不剥字符串），
            // 精确提取（剥注释/字符串）判定其不存在 → 复核须放行，不得误拦写入。
            File.WriteAllText(Path.Combine(pdIdx, "Docs.cs"),
                "var sample = @\"\nclass ExampleSpec\n\";\npublic class Docs { }\n");
            File.WriteAllText(Path.Combine(pdIdx, "Types.ts"), "class TsOnly { }\n");
            // 库文件放 pdIdx 之外：SqliteConnection 默认开启连接池，方法返回后连接回池但文件句柄
            // 仍被持有，放目录内会导致末尾 Directory.Delete(pdIdx) 抛"文件被另一进程占用"。
            var pdDb = Path.Combine(TestTmp, "pdi_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var pdStore = new Agent.Infrastructure.Project.SymbolIndexStore(pdDb);
            pdStore.BuildAndSave(pdIdx);

            var pdTarget = Path.Combine(pdIdx, "New.cs");
            var pdMap = Agent.Infrastructure.Project.TypeConflictDetector.GetExistingTypes(pdIdx, pdTarget, pdStore);
            Check("P-D 索引路径：命中 .cs 已有类型（Order/OrderService/Docs）",
                pdMap.ContainsKey("Order") && pdMap.ContainsKey("OrderService") && pdMap.ContainsKey("Docs"));
            Check("P-D 索引路径：非 .cs 文件符号不计入（.ts 与全扫口径一致）",
                !pdMap.ContainsKey("TsOnly"));
            var pdDup = Agent.Infrastructure.Project.TypeConflictDetector.DetectDuplicateTypes(
                pdIdx, pdTarget, "public class Order { }\n", pdStore);
            Check("P-D 索引路径：真重复类型 → 拦截（含定义文件）",
                pdDup.Count == 1 && pdDup[0].Contains("Order") && pdDup[0].Contains("Order.cs"));
            var pdPhantom = Agent.Infrastructure.Project.TypeConflictDetector.DetectDuplicateTypes(
                pdIdx, pdTarget, "public class ExampleSpec { }\n", pdStore);
            Check("P-D 索引路径：字符串型幻影经精确复核 → 不拦截",
                pdPhantom.Count == 0);
            var pdFallback = Agent.Infrastructure.Project.TypeConflictDetector.DetectDuplicateTypes(
                pdIdx, pdTarget, "public class Order { }\n");
            Check("P-D 回退全扫（无 store）：行为不变 → 拦截",
                pdFallback.Count == 1 && pdFallback[0].Contains("Order"));

            Directory.Delete(fgDir, true);
            Directory.Delete(pdIdx, true);
        }
    }

    // ── 区段 1486：编辑冲突诊断（EditFileTool 找不到 old_string 且备份漂移） ──
    public static void Run1486()
    {
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
    }

    // ── 区段 1516：工程只读模式（写工程文件被拒绝并提示） ──
    public static void Run1516()
    {
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
    }

    // ── 区段 1551：TestRunnerService（测试命令检测）+ ProcessRunner（执行单一原语） ──
    public static void Run1551()
    {
        Console.WriteLine("== TestRunnerService（测试命令检测）+ ProcessRunner（执行单一原语） ==");
        {
            // ── 【10-14 P4-16】ProcessRunner 契约：成功取双流 / 超时杀进程树并回部分输出 / ct 取消同路径 ──
            var prOk = Agent.Infrastructure.Execution.ProcessRunner.RunAsync(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c echo out-ok && echo err-ok 1>&2"
                }, timeoutMs: 30_000, CancellationToken.None).GetAwaiter().GetResult();
            Check("ProcessRunner：成功退出 → 退出码 0 + 双流分别回收",
                prOk.ExitCode == 0 && !prOk.TimedOut
                && prOk.Stdout.Contains("out-ok") && prOk.Stderr.Contains("err-ok"));

            var prTimeout = Agent.Infrastructure.Execution.ProcessRunner.RunAsync(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping -n 30 127.0.0.1 > nul"
                }, timeoutMs: 800, CancellationToken.None).GetAwaiter().GetResult();
            Check("ProcessRunner：超时 → TimedOut=true 且不抛异常（调用方按超时语义处理）",
                prTimeout.TimedOut);

            using (var prCts = new CancellationTokenSource())
            {
                prCts.Cancel();
                var prCancel = Agent.Infrastructure.Execution.ProcessRunner.RunAsync(
                    new System.Diagnostics.ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c echo x" },
                    timeoutMs: 0, prCts.Token).GetAwaiter().GetResult();
                Check("ProcessRunner：外部取消 → 与超时同路径（TimedOut=true）",
                    prCancel.TimedOut);
            }

            // 【10-14 P0】词边界判定单点断言（正/负例）
            Check("IsTestProjectName：独立词与 PascalCase 段命中",
                Agent.Infrastructure.Project.TestRunnerService.IsTestProjectName("MyApp.Tests")
                && Agent.Infrastructure.Project.TestRunnerService.IsTestProjectName("UnitTests")
                && Agent.Infrastructure.Project.TestRunnerService.IsTestProjectName("MyProjectTests"));
            Check("IsTestProjectName：Contestant/MyTester/Latest/Testing 不命中",
                !Agent.Infrastructure.Project.TestRunnerService.IsTestProjectName("Contestant")
                && !Agent.Infrastructure.Project.TestRunnerService.IsTestProjectName("MyTester")
                && !Agent.Infrastructure.Project.TestRunnerService.IsTestProjectName("Latest")
                && !Agent.Infrastructure.Project.TestRunnerService.IsTestProjectName("Testing"));

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

            // 【10-14 P1-5】词边界负例迁入（原 TestProjectDetector 区段随类删除）：
            // 仅含 Contestant.csproj（子串含 test）的工程不识别为测试工程
            var trDir5 = Path.Combine(TestTmp, "tr5_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(trDir5);
            File.WriteAllText(Path.Combine(trDir5, "Contestant.csproj"), "<Project Sdk='Microsoft.NET.Sdk' />");
            Check("Contestant.csproj（子串含 test）→ DetectTest null（词边界）",
                Agent.Infrastructure.Project.TestRunnerService.DetectTest(trDir5) is null);
            Directory.Delete(trDir5, true);

            Directory.Delete(trDir, true);
            Directory.Delete(trDir2, true);
            Directory.Delete(trDir3, true);
            Directory.Delete(trDir4, true);
        }
    }
}
