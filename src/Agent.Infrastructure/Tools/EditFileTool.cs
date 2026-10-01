// ============================================================
// EditFileTool.cs
// 局部修改文件。old_string 必须在文件中唯一出现。
// ============================================================

using System.Text.Json;

namespace Agent.Infrastructure.Tools;

/// <summary>
/// 局部编辑文件的工具。
/// 通过 old_string / new_string 精确替换文件片段。
/// </summary>
public sealed class EditFileTool : AgentToolBase
{
    private readonly string _workspaceRoot;

    /// <summary>构造函数。</summary>
    public EditFileTool(string workspaceRoot)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    /// <summary>工具名。</summary>
    public override string Name => "edit_file";

    /// <summary>工具描述。</summary>
    public override string Description =>
        "在已有文件中精确替换一段文本。old_string 必须在文件中唯一出现（否则会失败）。" +
        "适合修改单个函数/类/配置项，比 write_file 整体重写更安全。" +
        "使用前必须先用 read_file 查看文件当前内容。";

    /// <summary>参数 Schema。</summary>
    protected override string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "path": {
          "type": "string",
          "description": "相对于工作区根目录的文件路径"
        },
        "old_string": {
          "type": "string",
          "description": "要被替换的原文，必须唯一"
        },
        "new_string": {
          "type": "string",
          "description": "替换后的新文本"
        }
      },
      "required": ["path", "old_string", "new_string"]
    }
    """;

    /// <summary>执行工具。</summary>
    public override async Task<string> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var relative = args.GetProperty("path").GetString()!;
        var oldStr = args.GetProperty("old_string").GetString()!;
        var newStr = args.GetProperty("new_string").GetString()!;
        // 【校验 1】拒绝空 old_string
        // 空 old_string 在 C# string.IndexOf 里会返回 0，但替换逻辑会出错
        if (string.IsNullOrEmpty(oldStr))
        {
            return "错误：old_string 不能为空。如需向空文件写入内容，请用 write_file。";
        }

        var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relative));

        // 越界检查
        if (!fullPath.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase))
            return "错误：路径越界。";

        // 文件不存在
        if (!File.Exists(fullPath))
            return $"错误：文件不存在：{relative}。请先用 write_file 创建。";

        // 读取内容
        var content = await File.ReadAllTextAsync(fullPath, ct);

        // 找到第一次出现位置
        var index = content.IndexOf(oldStr, StringComparison.Ordinal);

        if (index < 0)
            return $"错误：在 {relative} 中找不到 old_string。请先用 read_file 查看文件当前内容。";

        // 检查唯一性
        var secondIndex = content.IndexOf(oldStr, index + 1, StringComparison.Ordinal);
        if (secondIndex >= 0)
            return $"错误：old_string 在 {relative} 中出现了多次，请扩大上下文使其唯一。";

        // 执行替换
        var updated = content[..index] + newStr + content[(index + oldStr.Length)..];

        // 写回
        await File.WriteAllTextAsync(fullPath, updated, ct);

        return $"已修改 {relative}：替换 {oldStr.Length} 字符 → {newStr.Length} 字符。";
    }
}