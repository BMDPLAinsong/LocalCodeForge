// ============================================================
// Agent.Infrastructure.Tests / Program.cs
// 确定性回归（零依赖断言 harness，无需 nuget 测试框架）。
// 批次5（10-13）：tests 单文件治理——本文件瘦身为"入口 + Harness 接线 +
// 按原行号顺序调用 Suites_* 主题文件的 Run 方法"。
// 91 个区段标题的输出顺序 = 下方调用顺序 = 原 Program.cs 行号顺序
// （99 → 3885；标题行号映射表见 sections_baseline.txt，Python 脚本核对防错位）。
// 运行：dotnet run --project tests/Agent.Infrastructure.Tests
// 退出码：0 = 全部通过；1 = 存在失败。
// ============================================================
using AgentTests;
using static AgentTests.TestHarness;

// 测试临时根：工程盘盘根（§14-3v 不在 C 盘 Temp；不硬编码本地路径）。
// 【1】test-tmp 清理：启动时删除 >7 天的旧测试目录，保留近期目录避免并发测试互删。
var testTmpRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? @"X:\", "law-test-tmp");
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
TestTmpRoot = testTmpRoot;
TestTmp = Path.Combine(testTmpRoot, Environment.ProcessId.ToString());
Directory.CreateDirectory(TestTmp);

// ── 按原行号顺序调用 91 个区段（Suites_* 主题文件）──
Suites_OutputVerifier.Run99();
Suites_OutputVerifier.Run106();
Suites_OutputVerifier.Run113();
Suites_OutputVerifier.Run120();
Suites_OutputVerifier.Run132();
Suites_Tools.Run144();
Suites_Tools.Run172();
Suites_Tools.Run231();
Suites_Memory.Run430();
Suites_Memory.Run463();
Suites_Memory.Run522();
Suites_Memory.Run570();
Suites_Memory.Run607();
Suites_Routing.Run634();
Suites_Routing.Run645();
Suites_OutputVerifier.Run660();
Suites_PathGuard.Run715();
Suites_Routing.Run756();
Suites_Routing.Run782();
Suites_Routing.Run804();
Suites_Routing.Run825();
Suites_Routing.Run847();
Suites_Routing.Run869();
Suites_Routing.Run906();
Suites_Routing.Run933();
Suites_Routing.Run971();
Suites_Routing.Run1055();
Suites_Routing.Run1121();
Suites_Tools.Run1167();
Suites_Tools.Run1227();
Suites_Project.Run1411();
Suites_Project.Run1449();
Suites_Project.Run1486();
Suites_Project.Run1516();
Suites_Project.Run1551();
Suites_Backup.Run1591();
Suites_Tools.Run1622();
Suites_Tools.Run1713();
Suites_Memory.Run1732();
Suites_Tools.Run1800();
Suites_Tools.Run1867();
Suites_Tuning.Run1948();
Suites_Tuning.Run1976();
Suites_Tuning.Run2004();
Suites_Tools.Run2046();
Suites_Backup.Run2076();
Suites_Tools.Run2104();
Suites_OutputVerifier.Run2130();
Suites_OutputVerifier.Run2169();
Suites_OutputVerifier.Run2200();
Suites_Tools.Run2256();
Suites_OutputVerifier.Run2278();
Suites_Backup.Run2304();
Suites_Backup.Run2330();
Suites_Backup.Run2353();
Suites_Backup.Run2432();
Suites_Backup.Run2450();
Suites_DiffSpectre.Run2461();
Suites_DiffSpectre.Run2530();
Suites_PathGuard.Run2596();
Suites_PathGuard.Run2606();
Suites_PathGuard.Run2665();
Suites_PathGuard.Run2695();
Suites_PathGuard.Run2715();
Suites_PathGuard.Run2727();
Suites_PathGuard.Run2773();
Suites_PathGuard.Run2809();
Suites_PathGuard.Run2848();
Suites_PathGuard.Run2872();
Suites_PathGuard.Run2907();
Suites_PathGuard.Run2918();
Suites_PathGuard.Run2947();
Suites_PathGuard.Run3016();
Suites_PathGuard.Run3057();
Suites_Routing.Run3100();
await Suites_Dashboard.Run3150();
await Suites_Dashboard.Run3535();
Suites_Tools.Run3591();
Suites_Routing.Run3636();
Suites_Routing.Run3645();
Suites_Tools.Run3656();
Suites_Config.Run3667();
Suites_Config.Run3690();
Suites_Config.Run3713();
Suites_Memory.Run3728();
Suites_Config.Run3750();
await Suites_Memory.Run3762();
Suites_Tools.Run3782();
Suites_Tools.Run3788();
Suites_Tools.Run3803();
Suites_Tools.Run3824();
Suites_OutputVerifier.Run3873();
Suites_OutputVerifier.Run3885();

// ── 批次6 ⑧ 能力分层断言（无标题，追加于 91 区段之后，不进入 baseline 顺序核验）──
Suites_Routing.RunToolLayering();

// ── 批次7 ⑨⑩ 断言（无标题，追加于 91 区段之后）──
Suites_Routing.RunDeterministicClassifier();
await Suites_Dashboard.RunEventSchemaVersion();

// ── 10-17 ②③ 断言（无标题，追加于 91 区段之后）──
Suites_Memory.RunMemoryTermsIndex();

Console.WriteLine($"\n结果: {Pass} 通过 / {Fail} 失败");
return Fail == 0 ? 0 : 1;
