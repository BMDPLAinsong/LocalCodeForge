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

    // ── 区段 1449：TestProjectDetector（完成门） ──
    public static void Run1449()
    {
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

    // ── 区段 1551：TestRunnerService（测试命令检测） ──
    public static void Run1551()
    {
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
    }
}
