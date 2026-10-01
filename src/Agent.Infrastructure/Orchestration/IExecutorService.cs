using Agent.Core.Models;

namespace Agent.Infrastructure.Orchestration;

public interface IExecutorService
{
    /// <summary>根据 FileSpec 生成文件内容。</summary>
    /// <param name="spec">文件规格</param>
    /// <param name="plan">完整计划</param>
    /// <param name="generatedFiles">
    /// 已生成文件的内容（path → content）。
    /// 生成后续文件时可参考前面的实际接口。
    /// </param>
    /// <param name="ct">取消令牌</param>
    Task<string> GenerateFileAsync(
        FileSpec spec,
        Plan plan,
        IReadOnlyDictionary<string, string> generatedFiles,
        CancellationToken ct);

    /// <summary>修复文件的编译/运行错误。</summary>
    Task<string> FixFileAsync(
        FileSpec spec,
        string currentContent,
        string errorMessage,
        Plan plan,
        IReadOnlyDictionary<string, string> allFiles,
        CancellationToken ct);
}