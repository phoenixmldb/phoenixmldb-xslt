namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Marks an exception as having arisen while evaluating a GLOBAL variable or parameter.
/// </summary>
/// <remarks>
/// <para>
/// A global whose evaluation fails is not fatal at load time — an unused global with a bad
/// select must not break the transformation — so the failure is captured and re-raised from a
/// <see cref="LazyValue"/> the first time something reads the global. That rethrow happens at
/// the point of USE, which may be inside <c>xsl:try</c>.
/// </para>
/// <para>
/// XSLT 3.0 evaluates global variables outside the dynamic scope of any <c>xsl:try</c>, so such
/// an error must stay uncatchable no matter where the deferred rethrow lands (xslt30-test
/// insn/try try-028). The mark rides on <see cref="System.Exception.Data"/> rather than a typed
/// property because the error can arrive as any of several exception types — FOAR0001 from
/// <c>22 div $p</c> surfaces as an XQuery exception, which has no XSLT-specific flag to set.
/// </para>
/// </remarks>
internal static class DeferredGlobalError
{
    private const string Key = "PhoenixmlDb.Xslt.DeferredGlobalError";

    /// <summary>Marks <paramref name="ex"/> as a deferred global-variable evaluation error.</summary>
    public static void Mark(System.Exception ex) => ex.Data[Key] = true;

    /// <summary>Reports whether <paramref name="ex"/> carries the mark.</summary>
    public static bool IsMarked(System.Exception ex) => ex.Data.Contains(Key);
}
