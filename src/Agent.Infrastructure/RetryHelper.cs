// ============================================================
// RetryHelper.cs
// 异步操作重试工具。用于 LLM 调用等可能临时失败的操作。
// ============================================================

using Microsoft.Extensions.Logging;

namespace Agent.Infrastructure;

/// <summary>
/// 重试辅助类。指数退避重试。
/// </summary>
public static class RetryHelper
{
    /// <summary>
    /// 带重试的异步执行。
    /// </summary>
    /// <typeparam name="T">返回类型</typeparam>
    /// <param name="action">要执行的异步操作</param>
    /// <param name="maxAttempts">最大尝试次数（含首次）</param>
    /// <param name="operationName">操作名，用于日志</param>
    /// <param name="logger">日志</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>操作结果</returns>
    /// <exception cref="OperationCanceledException">用户取消时直接抛出，不重试</exception>
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> action,
        int maxAttempts,
        string operationName,
        ILogger logger,
        CancellationToken ct)
    {
        Exception? lastEx = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户主动取消，直接抛出，不重试
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;

                logger.LogWarning(
                    ex,
                    "{Op} 第 {N}/{Max} 次尝试失败：{Msg}",
                    operationName, attempt, maxAttempts, ex.Message);

                if (attempt < maxAttempts)
                {
                    // 指数退避：1s, 2s, 4s, ...
                    var delaySeconds = Math.Pow(2, attempt - 1);
                    var delay = TimeSpan.FromSeconds(delaySeconds);

                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }
            }
        }

        logger.LogError(lastEx, "{Op} 达到最大重试次数 {Max}，放弃。", operationName, maxAttempts);
        throw lastEx!;
    }
}