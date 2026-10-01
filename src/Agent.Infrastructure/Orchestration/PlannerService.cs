// ============================================================
// PlannerService.cs
// 用 AgentPro 模型生成结构化 Plan。
// ============================================================

using System.Text.Json;
using System.Text.RegularExpressions;
using Agent.Core.Models;
using Agent.Infrastructure.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Agent.Infrastructure.Orchestration;

/// <summary>
/// 规划器实现。
/// </summary>
public sealed class PlannerService : IPlannerService
{
    private readonly Kernel _kernel;
    private readonly ILogger<PlannerService> _logger;

    public PlannerService(Kernel kernel, ILogger<PlannerService> logger)
    {
        _kernel = kernel;
        _logger = logger;
    }

    /// <summary>生成 Plan。</summary>
    /// <summary>生成 Plan。</summary>
    public async Task<Plan?> GeneratePlanAsync(string userInput, string workspaceRoot, CancellationToken ct)
    {
        // 【关键】输入长度保护
        const int MaxInputLength = 500;
        if (userInput.Length > MaxInputLength)
        {
            _logger.LogWarning(
                "输入长度 {Len} 超过 {Max}，截断处理。",
                userInput.Length, MaxInputLength);
            userInput = userInput[..MaxInputLength] + "\n...(输入过长，已截断，请基于已有信息规划)";
        }

        // 取 AgentPro 命名服务
        var chat = _kernel.GetRequiredService<IChatCompletionService>(RoutingDecision.AgentPro);

        // 构造对话
        var history = new ChatHistory();

        // ═══════════ System Prompt ═══════════
        history.AddSystemMessage("""
你是软件架构师。根据用户需求，输出一份 JSON 格式的实现计划。

## 输出格式（严格遵守）
只输出一个 JSON 对象，不要 markdown 代码块，不要任何解释文字，不要"好的"、"以下是"这类开场白。

{
  "summary": "一句话总结",
  "language": "python | csharp | javascript",
  "files": [
    {
      "path": "相对路径",
      "purpose": "文件职责（一句话）",
      "requirements": "分号分隔的具体要求"
    }
  ],
  "run_command": "验证命令",
  "expected_output": "期望输出摘要"
}

## 规划规则（基础）
1. 文件顺序 = 依赖顺序。被依赖的先写。
2. 每个文件职责单一，不要混。
3. requirements 要具体可执行，如"支持负数;除零返回错误"，不要说"写好点"。
4. 文件数量控制在 6 个以内（越少越好，单文件能解决就单文件）。
5. 验证命令要真实可运行，如 python main.py 或 dotnet run。
6. C# 项目第一个文件必须是 .csproj，且零第三方包。
7. 每条 requirements 不超过 40 字——输出过长会被截断，导致 JSON 不完整、整个计划失败。

## 【规则 A】语言选择（最高优先级）
默认语言是 Python。

判断规则（必须严格按此顺序）：
1. 用户输入里出现 "用C#"、"C#项目"、"dotnet"、"使用C#" → 选 csharp
2. 用户输入里出现 "用JavaScript"、"用JS"、"node"、"用Node" → 选 javascript
3. 用户输入里出现 "用Java"、"用Go"、"用Rust"、"用C++" → 选对应语言
4. 其它所有情况（包括用户没说语言、说了英文如 "function/input/output"）→ 选 python

错误示例：用户说"写一个 function，input 是两个 integers，output 它们的 sum"
→ 应该选 python，不要因为英文关键词选 C#。

## 【规则 B】尊重用户指定的文件名（最高优先级）
如果用户输入里明确指定了文件名（如 "parse_csv.py"、"data_utils.py"），
Plan 里必须使用完全相同的文件名，不要改名成 main.py。

例：
- 用户："写一个 Python 脚本 parse_csv.py，读取..."
  → Plan 的 path 必须是 "parse_csv.py"，不要 "main.py"
- 用户："用 Python 写一个 foo.py"
  → Plan 的 path 必须是 "foo.py"

## 【规则 C】输入数据自包含
如果任务需要读取外部文件（CSV/JSON/数据库/API）：

唯一正确做法：示例数据创建逻辑必须内嵌在入口脚本顶部。
严禁规划独立的 create_sample.py / init_db.py 等文件——因为入口脚本经常忘记调用它们，
导致"文件生成了但数据不存在"的运行时错误。

标准模板（Python CSV，入口脚本顶部）：

import os
import csv
import sys

DATA_FILE = "sample.csv"

# 若数据文件不存在，先创建示例数据
if not os.path.exists(DATA_FILE):
    with open(DATA_FILE, 'w', encoding='utf-8', newline='') as f:
        writer = csv.writer(f)
        writer.writerow(["group", "value"])
        writer.writerow(["groupA", 10])
        writer.writerow(["groupA", 20])
        writer.writerow(["groupB", 30])

标准模板（Python JSON）：

import os
import json

DATA_FILE = "data.json"
if not os.path.exists(DATA_FILE):
    with open(DATA_FILE, 'w', encoding='utf-8') as f:
        json.dump({"items": [1, 2, 3]}, f, ensure_ascii=False, indent=2)

## 【规则 D】示例数据要包含"可验证的案例"
如果任务涉及 统计 / 检测 / 判断 / 验证 类，示例数据必须包含 2-3 个"正面案例"，
否则输出全 0 / 全 False，无法验证脚本是否真的工作。

按任务类型的具体规则：
- 统计空值 → 数据里要有 2-3 个空单元格
  例：CSV 里 A 行的 value 为空、B 行的 group 为空
- 统计词频 → 文本里要有重复的词
  例：apple banana apple cherry apple（apple 出现 3 次）
- 判断回文 → 数据里要有回文和非回文各 1-2 个
  例：level（回文）、hello（非回文）
- 检测重复 → 数据里要有重复项和非重复项
  例：[1, 2, 2, 3, 3, 3, 4]
- 筛选大于 N → 数据里要有 > N 和 <= N 的数字
- 素数判断 → 数据里要有素数和合数
- 奇偶判断 → 数据里要有奇数和偶数

在 requirements 里要明确写出："示例数据包含 X 个空单元格"、"示例数据包含 3 个重复词"等。

## 【规则 E】脚本必须无参数可运行
由于 run_command 会被简化为 python main.py（不带参数），
入口脚本必须能在无参数情况下跑通。

具体要求：
1. argparse / click 的所有参数必须有 default 值
2. 默认值必须与脚本创建的示例数据列名一致
3. 不要依赖命令行传参才能跑通

正确示例：

parser.add_argument("--input", default="sample.csv")
parser.add_argument("--group_col", default="category")
parser.add_argument("--value_col", default="value")
parser.add_argument("--output", default="output.csv")

## 【规则 F】C# 项目结构（软引导）
C# 项目的文件组织原则：

判断标准：
- **1 个业务类**（如只有 Calculator）→ 可以合并到 1 个 .cs 文件
- **2+ 个业务类**（如 Calculator + Logger + UserManager）→ 每个类单独 1 个 .cs 文件
- **入口 Program.cs 永远单独**（含 Main 方法）

正确示例（简单任务，单文件）：
- Calculator.csproj
- Calculator.cs（包含 Calculator 类 + Program 类 + Main）

正确示例（复杂任务，多文件）：
- OrderSystem.csproj
- Program.cs（只放 Program 类 + Main）
- Order.cs（Order 类）
- OrderService.cs（OrderService 类）
- PaymentError.cs（自定义异常）

硬性要求（不论单文件还是多文件）：
1. **每个类只在一个文件里定义**——不要在两个文件里重复定义同一个类
2. **入口文件（Program.cs）只放 Program 类 + Main 方法**——不放业务类
3. **不要在 .cs 文件里写 `using <项目名>;`**——项目名不是命名空间
4. **不要为了"看起来模块化"而拆分**——简单任务就单文件

如果任务本身简单（如"写一个计算器"），优先单文件。
如果任务复杂（如"写一个订单系统，含订单、商品、用户"），用多文件。

## C# 项目 csproj 模板
严格照抄这个模板：

<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
</Project>

- TargetFramework 必须是 net10.0（不要 net5.0 / net6.0 / net8.0）
- 不要写 <Compile Include="xxx.cs" />（.NET SDK 自动包含）

## 【规则 G】C# 多文件 / 单文件的最终决策
按【规则 F】判断结构；**用户明确要求"多文件组织 / 模块化 / 拆分成多个文件"时，无论任务简单与否，都必须输出多文件**；否则按复杂度判断：1 个业务类 → 单文件，2+ 个业务类 → 多文件。

单文件时的类顺序：
1. 自定义异常类（如 DivisionError）
2. 业务类（如 Calculator）
3. Program 类（含 Main）

多文件 C# 的硬性要求（保证编译通过）：
1. 每个 .cs 文件只定义自己的类，**不要重复定义**
2. Program.cs 只放 Program 类 + Main，不放业务类
3. **不要使用命名空间**——所有类放 global namespace，跨文件引用无需任何 using，最稳定
4. **每个文件自带所需的 using**（csproj 是 ImplicitUsings disable，漏写会编译失败）：
   例：Program.cs 用 Console 要 `using System;`；OrderService.cs 若用 List 要 `using System.Collections.Generic;`
5. 不要写 `using <项目名>;`——项目名不是命名空间
6. 类与文件名一致：Order 类 → Order.cs，OrderService 类 → OrderService.cs
7. **csproj 项目名要与任务相关**：仓储系统 → RepositorySystem.csproj、日志系统 → LogSystem.csproj、图形面积 → AreaCalculator.csproj；**不要沿用与任务无关的名字**（如任务与订单无关时，禁止叫 OrderSystem.csproj）
8. **requirements 里不要写任何 using 指令**：每文件需要的 using（如 using System;）由生成代码时按需添加；写了 `using <类名>;` 或 `using <项目名>;` 会误导编译

正确示例（用户要求多文件，订单系统）：
- { "path": "OrderSystem.csproj", "purpose": "项目配置", "requirements": "net10.0;无第三方包;OutputType=Exe" }
- { "path": "Order.cs", "purpose": "订单模型", "requirements": "定义 Order 类（Id、Status 属性）；using System;" }
- { "path": "OrderService.cs", "purpose": "订单服务", "requirements": "定义 OrderService 类（Process 方法，接收 Order 返回新状态）；using System;" }
- { "path": "Program.cs", "purpose": "入口，只放 Program 类 + Main", "requirements": "using System; 创建 Order 和 OrderService，演示处理流程并打印结果" }

## 示例（Python CSV 统计）
用户：写一个 Python 脚本 parse_csv.py，统计 CSV 每列的空值数量

输出：
{
  "summary": "统计 CSV 每列空值数量",
  "language": "python",
  "files": [
    {
      "path": "parse_csv.py",
      "purpose": "读取 CSV 统计每列空值",
      "requirements": "自动创建 sample.csv（含 2 个空单元格：A 行的 value 为空、B 行的 group 为空）；用 csv.DictReader 读取；统计每列空值；输出格式'列名: N 个空值'"
    }
  ],
  "run_command": "python parse_csv.py",
  "expected_output": "group: 1 个空值\nvalue: 1 个空值"
}
""");

        // 用户输入
        history.AddUserMessage($"""
工作区：{workspaceRoot}
用户需求：{userInput}

请输出 Plan JSON：
""");

        // 温度 0.1（比 0.0 稳定，减少空输出）
        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.1,
            ExtensionData = new Dictionary<string, object>
            {
                ["think"] = false
            }
        };

        // ═══════════ 第一次尝试 ═══════════
        var raw = await CallAsync(chat, history, settings, ct);

        // ═══════════ 空输出重试（最多 3 次） ═══════════
        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogWarning("Planner 第一次返回空输出，追加提示后重试。");
            history.AddUserMessage("上次没有输出任何内容。请输出 JSON 格式的 Plan，只输出 JSON。");
            raw = await CallAsync(chat, history, settings, ct);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogWarning("Planner 第二次仍返回空输出，第三次尝试。");
            history.AddUserMessage("你必须在这次回复中输出一个 JSON 对象。从 { 开始，到 } 结束。不要输出任何其它文字。");
            raw = await CallAsync(chat, history, settings, ct);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogWarning("Planner 三次尝试均返回空输出，放弃。");
            return null;
        }

        _logger.LogInformation("Planner 原始输出：{Raw}", raw);

        // ═══════════ 尝试解析 ═══════════
        var plan = TryParsePlan(raw);
        if (plan is not null)
        {
            return SimplifyRunCommand(plan, userInput);
        }

        // ═══════════ 解析失败，重试（最多 2 次） ═══════════
        _logger.LogWarning(
            "Plan 解析失败，重试第 1/2 次。原始输出前 500 字符：{Raw}",
            raw.Length > 500 ? raw[..500] : raw);

        history.AddAssistantMessage(raw);
        history.AddUserMessage(
            "上面的输出不是合法 JSON，很可能是被截断了（requirements 写太长）。" +
            "请重新输出一个完整 JSON 对象：从 { 开始，到 } 结束；" +
            "每条 requirements 不超过 40 字，files 不超过 6 个；只输出 JSON，不要任何其它文字。");

        var raw2 = await CallAsync(chat, history, settings, ct);
        if (!string.IsNullOrWhiteSpace(raw2))
        {
            var plan2 = TryParsePlan(raw2);
            if (plan2 is not null)
            {
                return SimplifyRunCommand(plan2, userInput);
            }
        }

        // 第 2 次重试：升级到 Large（§14-3l 规划失败升级链——同模型再试收益有限，
        // 4B 生成的 JSON 截断（requirements 过长）往往再试仍截断，大模型一次机会）
        _logger.LogWarning(
            "Plan 再次解析失败，升级到 Large 重试。原始输出前 500 字符：{Raw}",
            (raw2 ?? string.Empty).Length > 500
                ? (raw2 ?? string.Empty)[..500]
                : (raw2 ?? string.Empty));

        chat = _kernel.GetRequiredService<IChatCompletionService>(RoutingDecision.Large);

        if (!string.IsNullOrWhiteSpace(raw2))
        {
            history.AddAssistantMessage(raw2);
        }
        history.AddUserMessage(
            "上次输出仍是无效或不完整的 JSON。这次必须输出完整 JSON 结构：" +
            "{ \"summary\": ..., \"language\": ..., \"files\": [{\"path\": ..., \"purpose\": ..., \"requirements\": ...}], \"run_command\": ..., \"expected_output\": ... }。" +
            "files 不超过 6 个，每条 requirements 不超过 40 字，不要写 using 指令。");

        var raw3 = await CallAsync(chat, history, settings, ct);
        if (string.IsNullOrWhiteSpace(raw3)) return null;

        var plan3 = TryParsePlan(raw3);
        return plan3 is not null ? SimplifyRunCommand(plan3, userInput) : null;
    }

    /// <summary>单次调用 LLM（带重试）。</summary>
    private async Task<string?> CallAsync(
        IChatCompletionService chat,
        ChatHistory history,
        OpenAIPromptExecutionSettings settings,
        CancellationToken ct)
    {
        try
        {
            return await RetryHelper.RetryAsync(
                action: async () =>
                {
                    var response = await chat.GetChatMessageContentAsync(
                        history, settings, cancellationToken: ct);
                    return response.Content;
                },
                maxAttempts: 3,
                operationName: "Planner.Call",
                logger: _logger,
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Planner 调用失败（已重试 3 次）。");
            return null;
        }
    }

    /// <summary>从 LLM 输出里提取 Plan JSON。</summary>
    private static Plan? TryParsePlan(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 剥离 markdown 代码块
        var cleaned = Regex.Replace(
            text,
            @"```(?:json)?\s*([\s\S]*?)```",
            "$1",
            RegexOptions.IgnoreCase).Trim();

        // 找 JSON 主体
        var jsonStr = ExtractFirstJsonObject(cleaned);
        if (jsonStr is null) return null;

        try
        {
            return JsonSerializer.Deserialize<Plan>(jsonStr, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从文本中提取第一个平衡的 JSON 对象。</summary>
    private static string? ExtractFirstJsonObject(string s)
    {
        var start = s.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return s.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    /// <summary>
    /// 后处理 Plan：
    ///   0. 提取用户指定文件名（如 parse_csv.py），强制入口文件用该名
    ///   1. 强制简化 run_command
    ///   2. 替换禁词（matplotlib / requests 等）
    ///   3. 逐文件处理：过滤示例文件、注入数据文件创建、注入 argparse default、注入 csproj TargetFramework、入口文件改名
    ///   4. 重建 Plan
    /// </summary>
    private static Plan SimplifyRunCommand(Plan plan, string userInput)
    {
        // ─────────── 0. 提取用户指定文件名 ───────────
        var requestedFileName = ExtractRequestedFileName(userInput, plan.Language);

        // 判断某个路径是否是该语言的"入口文件"
        static bool IsEntryFile(string path, string language)
        {
            var fileName = Path.GetFileName(path).ToLowerInvariant();
            return language switch
            {
                "python" => fileName == "main.py" || fileName == "app.py" || fileName == "run.py",
                "javascript" => fileName == "main.js" || fileName == "index.js" || fileName == "app.js",
                _ => false
            };
        }

        var entryFileRenamed = false;
        string? finalEntryFileName = null;

        // ─────────── 1. 简化 run_command ───────────
        // 先按原逻辑简化；如果入口文件被改名，后面会用新名重建
        var original = plan.RunCommand;
        var simplified = original;

        if (!string.IsNullOrWhiteSpace(original))
        {
            // 提取最后一个 "python xxx.py"
            var pyMatches = Regex.Matches(original, @"\bpython3?\s+([^\s&;|]+\.py)\b");
            if (pyMatches.Count > 0)
            {
                var last = pyMatches[pyMatches.Count - 1];
                simplified = $"python {last.Groups[1].Value}";
            }
            else
            {
                var nodeMatch = Regex.Match(original, @"\bnode\s+([^\s&;|]+\.js)\b");
                if (nodeMatch.Success)
                {
                    simplified = $"node {nodeMatch.Groups[1].Value}";
                }
                else if (original.Contains("dotnet ", StringComparison.OrdinalIgnoreCase))
                {
                    simplified = "dotnet run";
                }
            }
        }

        // ─────────── 2. 禁词替换表 ───────────
        var bannedReplacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["matplotlib.pyplot"] = "ASCII 柱状图",
            ["matplotlib"] = "ASCII 柱状图",
            ["plt.bar"] = "print('*' * n)",
            ["plt."] = "print('*' * n)  // ",
            ["numpy"] = "list/dict",
            ["pandas"] = "csv 模块",
            ["flask"] = "标准库",
            ["django"] = "标准库",
            ["pillow"] = "标准库",
            ["PIL"] = "标准库",
        };

        // ─────────── 2.5 外部数据任务判定（P1-4）───────────
        // 外部任务（天气/汇率/行情等）需用 requests 访问 HTTP 服务（约束块/参考实现同款），
        // 不替换；本地任务仍强制 urllib.request（防乱用第三方库）。
        var isExternal = ExternalTaskDetector.Detect(userInput) is not null;

        // ─────────── 3. 逐文件处理 ───────────
        var sanitizedFiles = new List<FileSpec>();

        foreach (var f in plan.Files)
        {
            var req = f.Requirements ?? "";

            // 3.1 替换禁词
            // 【强化】禁词替换——同时检查 requirements 和 run_command
            foreach (var (bad, good) in bannedReplacements)
            {
                // requirements 替换
                if (req.Contains(bad, StringComparison.OrdinalIgnoreCase))
                {
                    req = Regex.Replace(req, Regex.Escape(bad), good, RegexOptions.IgnoreCase);
                }
            }

            // 【P1-4】requirements 残留 requests：仅本地任务强制改 urllib.request；外部任务保留
            if (!isExternal && req.Contains("requests", StringComparison.OrdinalIgnoreCase))
            {
                req = Regex.Replace(req, @"\brequests\b", "urllib.request", RegexOptions.IgnoreCase);
            }

            // 3.2 过滤独立示例数据文件（合并到入口文件）
            var fileNameNoExt = Path.GetFileNameWithoutExtension(f.Path).ToLowerInvariant();
            var isSampleFile =
                fileNameNoExt.Contains("create_sample") ||
                fileNameNoExt.Contains("init_db") ||
                fileNameNoExt.Contains("seed") ||
                fileNameNoExt.Contains("generate_data") ||
                fileNameNoExt.Contains("sample_data");

            if (isSampleFile)
            {
                continue;   // 跳过，其职责合并到 main.py
            }

            // 3.3 强制注入"数据文件不存在则创建"
            var isEntry = Path.GetFileName(f.Path).Equals("main.py", StringComparison.OrdinalIgnoreCase)
                       || Path.GetFileName(f.Path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase)
                       || Path.GetFileName(f.Path).Equals("index.js", StringComparison.OrdinalIgnoreCase);

            var mentionsFileRead =
                req.Contains("读取", StringComparison.OrdinalIgnoreCase) ||
                req.Contains("read", StringComparison.OrdinalIgnoreCase) ||
                req.Contains("输入文件", StringComparison.OrdinalIgnoreCase);

            var mentionsDataFile =
                req.Contains("CSV", StringComparison.OrdinalIgnoreCase) ||
                req.Contains("JSON", StringComparison.OrdinalIgnoreCase) ||
                req.Contains("data.csv", StringComparison.OrdinalIgnoreCase) ||
                req.Contains("数据文件", StringComparison.OrdinalIgnoreCase) ||
                req.Contains("sample.csv", StringComparison.OrdinalIgnoreCase) ||
                req.Contains("input.csv", StringComparison.OrdinalIgnoreCase);

            var alreadyHasCreateLogic =
                req.Contains("若不存在", StringComparison.OrdinalIgnoreCase) ||
                req.Contains("不存在则创建", StringComparison.OrdinalIgnoreCase) ||
                req.Contains("示例数据", StringComparison.OrdinalIgnoreCase) ||
                req.Contains("自动创建", StringComparison.OrdinalIgnoreCase);

            if (isEntry && mentionsFileRead && mentionsDataFile && !alreadyHasCreateLogic)
            {
                req += "；【强制】入口脚本第一行必须检测数据文件是否存在，不存在则用标准库创建3行示例数据（CSV 用 csv.writer；JSON 用 json.dump）";
            }

            // 3.3.5 【新增】强制注入"示例数据含正例"
            // 4B 经常忽略 Prompt，这里用代码强制
            var needsPositiveCase = DetectPositiveCaseRequirement(req);

            if (needsPositiveCase is not null
                && !req.Contains("包含", StringComparison.OrdinalIgnoreCase))
            {
                req += $"；【强制】{needsPositiveCase}";
            }

            // 3.4 强制注入"argparse 参数必须有默认值"
            var mentionsArgparse = req.Contains("argparse", StringComparison.OrdinalIgnoreCase);
            var mentionsArg = req.Contains("命令行参数", StringComparison.OrdinalIgnoreCase)
                           || req.Contains("命令行", StringComparison.OrdinalIgnoreCase);
            var alreadyHasDefault = req.Contains("default", StringComparison.OrdinalIgnoreCase)
                                 || req.Contains("默认值", StringComparison.OrdinalIgnoreCase);

            if ((mentionsArgparse || mentionsArg) && !alreadyHasDefault)
            {
                req += "；【强制】所有 argparse 参数必须有 default 值，确保无参数调用也能运行";
            }

            // 3.5 强制注入 csproj TargetFramework
            var isCsproj = f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
            if (isCsproj)
            {
                var alreadyHasNet10 = req.Contains("net10.0", StringComparison.OrdinalIgnoreCase);
                if (!alreadyHasNet10)
                {
                    req += "；【强制】TargetFramework 必须为 net10.0";
                }
            }

            // 3.6 入口文件改名
            var finalPath = f.Path;

            // 【新增】改名时检查扩展名是否和语言匹配
            if (requestedFileName is not null
                && !entryFileRenamed
                && IsEntryFile(f.Path, plan.Language)
                && IsExtensionMatchLanguage(requestedFileName, plan.Language))
            {
                finalPath = requestedFileName;
                entryFileRenamed = true;
                finalEntryFileName = requestedFileName;
            }

            sanitizedFiles.Add(new FileSpec
            {
                Path = finalPath,
                Purpose = f.Purpose,
                Requirements = req,
                Skeleton = f.Skeleton
            });
        }

        // 兜底：如果一个文件都没了（全被过滤），使用原始文件列表
        if (sanitizedFiles.Count == 0)
        {
            sanitizedFiles = plan.Files.ToList();
        }

        // ─────────── 4. 如果入口被改名，用新名重建 run_command ───────────
        if (finalEntryFileName is not null)
        {
            simplified = plan.Language switch
            {
                "python" => $"python {finalEntryFileName}",
                "javascript" => $"node {finalEntryFileName}",
                "csharp" => "dotnet run",
                _ => $"python {finalEntryFileName}"
            };
        }

        // ─────────── 5. 重建 Plan ───────────
        return new Plan
        {
            Summary = plan.Summary,
            Language = plan.Language,
            Files = sanitizedFiles,
            RunCommand = simplified,
            ExpectedOutput = plan.ExpectedOutput
        };
    }

    /// <summary>
    /// 从用户输入里提取指定的文件名。
    /// 支持：xxx.py / xxx.js / xxx.ts
    /// 排除：xxx.json / xxx.csv / xxx.txt（数据文件）
    /// </summary>
    private static string? ExtractRequestedFileName(string userInput, string language)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return null;

        // 不支持 C#（csproj 项目名和文件名独立）
        if (language == "csharp") return null;

        // 【关键】用词边界 \+ 负向断言，避免匹配 xxx.json / xxx.jsx 等
        // 解释：
        //   \b          词边界
        //   (?!...）    后面不能跟这些字符
        var pattern = @"\b([a-zA-Z_][\w\-]*\.(?:py|js|ts))(?![a-zA-Z0-9])";

        var matches = Regex.Matches(userInput, pattern);

        foreach (Match m in matches)
        {
            var fileName = m.Groups[1].Value;

            // 【双重保险】排除数据文件后缀
            var lower = fileName.ToLowerInvariant();
            if (lower.EndsWith(".json") ||
                lower.EndsWith(".jsonc") ||
                lower.EndsWith(".csv") ||
                lower.EndsWith(".txt") ||
                lower.EndsWith(".xml") ||
                lower.EndsWith(".yaml") ||
                lower.EndsWith(".yml") ||
                lower.EndsWith(".md"))
            {
                continue;
            }

            return fileName;
        }

        return null;
    }

    /// <summary>
    /// 检测需求里是否需要"示例数据含正例"，返回要注入的文本；不需要则返回 null。
    /// </summary>
    private static string? DetectPositiveCaseRequirement(string req)
    {
        // 空值统计
        if ((req.Contains("空值", StringComparison.OrdinalIgnoreCase) ||
             req.Contains("null", StringComparison.OrdinalIgnoreCase) ||
             req.Contains("缺失", StringComparison.OrdinalIgnoreCase)))
        {
            return "示例数据必须包含 2-3 个空单元格（如 CSV 中某些字段留空），让空值统计结果非 0";
        }

        // 词频统计
        if (req.Contains("词频", StringComparison.OrdinalIgnoreCase) ||
            req.Contains("单词", StringComparison.OrdinalIgnoreCase) ||
            req.Contains("word count", StringComparison.OrdinalIgnoreCase))
        {
            return "示例文本必须包含重复的词（如 'apple banana apple cherry apple'），让词频统计有区分度";
        }

        // 回文判断
        if (req.Contains("回文", StringComparison.OrdinalIgnoreCase) ||
            req.Contains("palindrome", StringComparison.OrdinalIgnoreCase))
        {
            return "示例数据必须包含回文（如 'level'）和非回文（如 'hello'）各 2 个";
        }

        // 重复检测
        if (req.Contains("重复", StringComparison.OrdinalIgnoreCase) ||
            req.Contains("去重", StringComparison.OrdinalIgnoreCase) ||
            req.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return "示例数据必须包含 2-3 个重复项和非重复项，让去重效果可见";
        }

        // 素数/质数
        if (req.Contains("素数", StringComparison.OrdinalIgnoreCase) ||
            req.Contains("质数", StringComparison.OrdinalIgnoreCase) ||
            req.Contains("prime", StringComparison.OrdinalIgnoreCase))
        {
            return "示例数据必须包含素数（2,3,5,7）和非素数（4,6,8,9）各若干";
        }

        // 奇偶判断
        if (req.Contains("奇数", StringComparison.OrdinalIgnoreCase) ||
            req.Contains("偶数", StringComparison.OrdinalIgnoreCase))
        {
            return "示例数据必须包含奇数和偶数各 2-3 个";
        }

        // 筛选/过滤
        if (req.Contains("筛选", StringComparison.OrdinalIgnoreCase) ||
            req.Contains("过滤", StringComparison.OrdinalIgnoreCase) ||
            req.Contains("filter", StringComparison.OrdinalIgnoreCase))
        {
            return "示例数据必须同时包含满足条件和不满足条件的数据";
        }

        // 不需要正例注入
        return null;
    }

    /// <summary>
    /// 检查文件扩展名是否和语言匹配。
    /// </summary>
    private static bool IsExtensionMatchLanguage(string fileName, string language)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return language switch
        {
            "python" => ext == ".py",
            "javascript" => ext == ".js" || ext == ".ts",
            "csharp" => ext == ".cs",
            _ => true
        };
    }
}