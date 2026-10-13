// ============================================================
// Suites_OutputVerifier.cs
// 批次5：tests 单文件治理拆分产物。
// 区段来源（原 Program.cs 行号）：99-143（凭据/stderr/结构/通用失败/占位/模板）、
//   660-713（模板检测降级+循环熔断签名）、2130-2167（A 演示分支容错）、
//   2169-2198（Lookup 演示容错）、2200-2254（证据链先行）、2278-2302（P0-2 严格校验）、
//   3873-3882（401/403 凭据信号收紧）、3885-3914（10-13 工具契约结构化）。
// 块体原样搬移，只改顶层全局引用（mPhOut/mTmpl → MPhOutPublic/MTmplPublic）。
// ============================================================
using System.Text.Json;
using static AgentTests.TestHarness;

namespace AgentTests;

public static class Suites_OutputVerifier
{
    // ── 区段 99：凭据失败（检查 0.5） ──
    public static void Run99()
    {
        Console.WriteLine("== 凭据失败（检查 0.5） ==");
        Check("401 JSON", Cred("错误: 401 - {\"cod\":401, \"message\": \"Invalid api key\"}"));
        Check("403 Forbidden", Cred("HTTP错误: 403 - Forbidden"));
        Check("api_key invalid", Cred("获取天气信息失败: 401 (api_key invalid)"));
        Check("unauthorized", Cred("Error: unauthorized access"));
        Check("正常输出不误伤", !Cred("IP: 8.8.8.8 / Temperature: 21°C / Humidity: 65%"));
    }

    // ── 区段 106：stderr 剥离（检查 2） ──
    public static void Run106()
    {
        Console.WriteLine("== stderr 剥离（检查 2） ==");
        var pureWarn = "C:\\...\\requests\\__init__.py:113: RequestsDependencyWarning: urllib3 (2.7.0) or chardet (7.4.3) doesn't match a supported version!\n  warnings.warn(\n    \"urllib3 ({0}) or chardet ({1}) doesn't match a supported version!\",\n    RequestsDependencyWarning)";
        Check("纯 Warning 剥离后为空", string.IsNullOrWhiteSpace(Strip(pureWarn)));
        var warnPlusErr = pureWarn + "\nTraceback (most recent call last):\nValueError: boom";
        Check("Warning+真错误 剥离后非空", !string.IsNullOrWhiteSpace(Strip(warnPlusErr)));
        Check("NETSDK 警告行被过滤", string.IsNullOrWhiteSpace(Strip("warning NETSDK1174: Ignoring imported project")));
    }

    // ── 区段 113：结构性错误（检查 3） ──
    public static void Run113()
    {
        Console.WriteLine("== 结构性错误（检查 3） ==");
        Check("error CS", Struct("Program.cs(8,9): error CS0272: 属性或索引器不可用"));
        Check("Traceback", Struct("Traceback (most recent call last):\n  NameError: name 'x' is not defined"));
        Check("ZeroDivisionError（并集新增）", Struct("ZeroDivisionError: division by zero"));
        Check("找不到（中文）", Struct("找不到文件 xxx"));
        Check("成功输出不误伤", !Struct("IP: 8.8.8.8 / Temperature: 21°C / Humidity: 65%"));
    }

    // ── 区段 120：通用失败词 + JSON + 双容错（检查 3.5） ──
    public static void Run120()
    {
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
    }

    // ── 区段 132：占位 Key / 占位符 / 模板（检查 4） ──
    public static void Run132()
    {
        Console.WriteLine("== 占位 Key / 占位符 / 模板（检查 4） ==");
        Check("占位 Key 命中", PhKey("请在代码中替换YOUR_OPENWEATHERMAP_API_KEY为您的实际API密钥"));
        Check("占位符过半命中", (bool)Invoke(MPhOutPublic, new object?[] { (IReadOnlyList<string>)new List<string> { "?", "N/A", "OK" }, null, null })!);
        bool PhOut(params string[] lines) => (bool)Invoke(MPhOutPublic, new object?[] { (IReadOnlyList<string>)lines.ToList(), null, null })!;
        Check("标题+数据行不判占位（fib 形态，P2-1）", !PhOut("Fibonacci 序列:", "0 1 1 2 3 5 8"));
        Check("单行纯标题壳判占位（P2-1）", PhOut("结果:"));
        Check("全空标题壳判占位（P2-1）", PhOut("订单:", "客户:"));
        Check("键值输出不判占位（P2-1）", !PhOut("IP: 8.8.8.8", "Temperature: 21°C"));
        Check("模板单行 hello 命中", (bool)Invoke(MTmplPublic, new object?[] { (IReadOnlyList<string>)new List<string> { "hello" } })!);
        Check("模板 success 命中", (bool)Invoke(MTmplPublic, new object?[] { (IReadOnlyList<string>)new List<string> { "success" } })!);
        Check("多行正常输出不判模板", !(bool)Invoke(MTmplPublic, new object?[] { (IReadOnlyList<string>)new List<string> { "IP: 8.8.8.8", "Temperature: 21°C" } })!);
    }

    // ── 区段 660：模板检测降级：任务要求输出豁免（方案 1） ──
    public static void Run660()
    {
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
    }

    // ── 区段 2130：10-08 A 演示分支容错（OutputVerifier.HasGenericFailure 容错 3） ──
    public static void Run2130()
    {
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
    }

    // ── 区段 2169：10-08 Lookup 演示容错（OutputVerifier.HasGenericFailure 容错 3b） ──
    public static void Run2169()
    {
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
    }

    // ── 区段 2200：10-08 A 证据链先行（HasSuccessEvidence + 4 参重载） ──
    public static void Run2200()
    {
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
    }

    // ── 区段 2278：P0-2 expected_output 严格校验（IsTaskExpectedOutputStrict） ──
    public static void Run2278()
    {
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
    }

    // ── 区段 3873：10-10 401/403 外部凭据信号收紧：排除 dotnet SDK 版本号（10.0.401）误伤 ──
    public static void Run3873()
    {
        Console.WriteLine("== 10-10 401/403 凭据信号收紧 ==");
        {
            string Pat(string s) { Agent.Infrastructure.Quality.OutputVerifier.HasCredentialFailure(s, out var p); return p; }
            Check("401 HTTP 上下文 → 命中", Agent.Infrastructure.Quality.OutputVerifier.HasCredentialFailure("HTTP/1.1 401 Unauthorized", out _));
            Check("403 status 上下文 → 命中", Agent.Infrastructure.Quality.OutputVerifier.HasCredentialFailure("status: 403 Forbidden", out _));
            Check("裸 401 → 命中", Agent.Infrastructure.Quality.OutputVerifier.HasCredentialFailure("error code 401", out _));
            Check("dotnet SDK 10.0.401 → 不误伤", !Agent.Infrastructure.Quality.OutputVerifier.HasCredentialFailure(@"C:\Program Files\dotnet\sdk\10.0.401\Microsoft.Common.CurrentVersion.targets", out _));
            Check("小数 0.401 → 不误伤", !Agent.Infrastructure.Quality.OutputVerifier.HasCredentialFailure("elapsed 0.401 seconds", out _));
            Check("长数字 1401 → 不误伤", !Agent.Infrastructure.Quality.OutputVerifier.HasCredentialFailure("count=1401", out _));
        }
    }

    // ── 区段 3885：10-13 批次4 工具契约结构化：ToolResult 字段 / Code 词表 / 兼容转发 ──
    public static void Run3885()
    {
        Console.WriteLine("== 10-13 工具契约结构化 ==");
        {
            // Ok/Fail 字段语义
            var ok = Agent.Core.Models.ToolResult.Ok("已写入 x.cs，共 3 字符。");
            Check("Ok.Success=true", ok.Success);
            Check("Ok.Code=null", ok.Code == null);
            Check("Ok.Message 逐字保留", ok.Message == "已写入 x.cs，共 3 字符。");
            var failRes = Agent.Core.Models.ToolResult.Fail(Agent.Core.Models.ToolErrorCodes.OutOfBounds, "错误：路径越界。");
            Check("Fail.Success=false", !failRes.Success);
            Check("Fail.Code=OutOfBounds", failRes.Code == Agent.Core.Models.ToolErrorCodes.OutOfBounds);
            Check("Fail.Message 逐字保留", failRes.Message == "错误：路径越界。");
            // ToString == Message（渲染/E2E/事件存储零冲击的验收标准）
            Check("ToString()==Message", failRes.ToString() == "错误：路径越界。");
            // 兼容转发：旧字符串用法编译且语义等价
            Check("转发 StartsWith 语义", failRes.StartsWith("错误："));
            Check("转发 Contains 语义", failRes.Contains("路径越界"));
            Check("转发 EndsWith 语义", ok.EndsWith("字符。"));
            Check("转发 Length 语义", ok.Length == ok.Message.Length);
            // implicit operator string
            string s = ok;
            Check("隐式转 string 取 Message", s == "已写入 x.cs，共 3 字符。");
            // 词表对齐：现成 rejection code（tool_rejections 表落库码）必须能在 ToolErrorCodes 找到
            Check("词表对齐 out_of_bounds", string.Equals(Agent.Core.Models.ToolErrorCodes.OutOfBounds, "out_of_bounds", StringComparison.OrdinalIgnoreCase) || Agent.Core.Models.ToolErrorCodes.OutOfBounds == "out_of_bounds");
            Check("词表对齐 pd_conflict", Agent.Core.Models.ToolErrorCodes.PdConflict == "pd_conflict");
            Check("词表对齐 read_only", Agent.Core.Models.ToolErrorCodes.ReadOnly == "read_only");
            Check("词表对齐 project_file", Agent.Core.Models.ToolErrorCodes.ProjectFile == "project_file");
            // 语义等价性：文本判定(IsToolSemanticFailure) 与 字段判定(!Success) 对同一 Message 结论一致
            Check("文本判定与字段判定等价(失败)", Agent.Infrastructure.AgentLoop.IsToolSemanticFailure(failRes.Message) == !failRes.Success);
            Check("文本判定与字段判定等价(成功)", Agent.Infrastructure.AgentLoop.IsToolSemanticFailure(ok.Message) == !ok.Success);
        }
    }
}
