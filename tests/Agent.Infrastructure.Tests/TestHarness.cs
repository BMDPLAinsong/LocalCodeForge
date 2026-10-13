// ============================================================
// TestHarness.cs
// 确定性回归测试的公共 Harness（批次5：tests 单文件治理拆分产物）。
// 原 Program.cs 的全部顶层依赖迁入此静态类：
//   · 断言计数器 pass/fail + Check(name, cond)
//   · 测试临时根 TestTmpRoot / TestTmp（Program 入口赋值）
//   · OutputVerifier 反射句柄（mCred..mTmpl）与包装函数 Cred/Strip/Generic/Struct/PhKey
//   · Invoke / M / TryCompileRegex
// 拆分文件通过 `using static AgentTests.TestHarness;` 裸引用这些成员，
// 保证区段块体原样搬移（只改顶层全局引用）。
// ============================================================
using System.Reflection;

namespace AgentTests;

/// <summary>全部测试区段的公共 Harness（static，无实例状态）。</summary>
public static class TestHarness
{
    // ── 断言计数器 ──
    public static int Pass;
    public static int Fail;

    public static void Check(string name, bool cond)
    {
        if (cond) { Pass++; Console.WriteLine($"  [OK] {name}"); }
        else { Fail++; Console.WriteLine($"  [FAIL] {name}"); }
    }

    // ── 测试临时根（Program 入口赋值；不在 C 盘 Temp，取工程盘盘根）──
    public static string TestTmpRoot = "";
    public static string TestTmp = "";

    // ── OutputVerifier 反射句柄（原 Program.cs 46-66 行）──
    private static readonly Type T = Assembly.Load("Agent.Infrastructure")
        .GetType("Agent.Infrastructure.Quality.OutputVerifier", throwOnError: true)!;

    public static object? Invoke(MethodInfo m, params object?[] args)
        => m.Invoke(null, args);

    public static MethodInfo M(Type t, string name, Type[] ptypes)
    {
        var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, ptypes, null)
            ?? throw new Exception("missing method " + name);
        return m;
    }

    private static readonly MethodInfo MCred = M(T, "HasCredentialFailure", new[] { typeof(string), typeof(string).MakeByRefType() });
    private static readonly MethodInfo MStrip = M(T, "StripHarmlessWarnings", new[] { typeof(string) });
    private static readonly MethodInfo MStruct = M(T, "HasStructuralError", new[] { typeof(string), typeof(string).MakeByRefType() });
    private static readonly MethodInfo MGeneric = M(T, "HasGenericFailure", new[] { typeof(string), typeof(string), typeof(string).MakeByRefType() });
    private static readonly MethodInfo MPhKey = M(T, "HasPlaceholderKey", new[] { typeof(IReadOnlyList<string>), typeof(string).MakeByRefType() });
    private static readonly MethodInfo MPhOut = M(T, "IsPlaceholderOutput", new[] { typeof(IReadOnlyList<string>), typeof(int).MakeByRefType(), typeof(int).MakeByRefType() });
    private static readonly MethodInfo MTmpl = M(T, "IsTemplateOutput", new[] { typeof(IReadOnlyList<string>) });

    // ── 包装函数（区段块体原样调用）──
    public static bool Cred(string output)
    {
        var args = new object?[] { output, null };
        var r = (bool)Invoke(MCred, args)!;
        return r;
    }

    public static string? Strip(string stderr) => (string?)Invoke(MStrip, stderr);

    public static bool Generic(string stdout, string? expected)
    {
        var args = new object?[] { stdout, expected, null };
        return (bool)Invoke(MGeneric, args)!;
    }

    public static bool Struct(string stdout)
    {
        var args = new object?[] { stdout, null };
        return (bool)Invoke(MStruct, args)!;
    }

    public static bool PhKey(params string[] lines) => (bool)Invoke(MPhKey, new object?[] { (IReadOnlyList<string>)lines.ToList(), null })!;

    /// <summary>区段 132 内曾引用 mPhOut/mTmpl 的裸 Invoke 调用 → 迁为 H.MPhOut / H.MTmpl。</summary>
    public static MethodInfo MPhOutPublic => MPhOut;
    public static MethodInfo MTmplPublic => MTmpl;

    /// <summary>正则可编译性校验（RoutingRules.json 自检区段使用）。</summary>
    public static bool TryCompileRegex(string pattern)
    {
        try { _ = new System.Text.RegularExpressions.Regex(pattern); return true; }
        catch { return false; }
    }
}
