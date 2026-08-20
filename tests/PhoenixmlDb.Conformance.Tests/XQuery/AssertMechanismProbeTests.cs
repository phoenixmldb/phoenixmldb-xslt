using PhoenixmlDb.XQuery;
using PhoenixmlDb.XQuery.Execution;
using Xunit;

namespace PhoenixmlDb.Conformance.Tests.XQuery;

/// <summary>
/// Pins the mechanism behind <c>XqtsTestRunner</c>'s plain-<c>&lt;assert&gt;</c> support:
/// compile the assertion with <c>$result</c> declared external, supply the query result
/// through <c>SetExternalVariable</c>, execute, take the effective boolean value.
///
/// These exist because the first implementation was INERT and the suite could not tell.
/// It compiled the bare expression, which fails static analysis on the undeclared
/// <c>$result</c>; the catch-all turned that into "assertion false", which is
/// indistinguishable from a genuine assertion failure. The QT3 pass rate moved by 2 tests
/// out of 31470 and nothing surfaced. A unit-level probe catches that in a second; a
/// 30-minute conformance sweep does not.
/// </summary>
public sealed class AssertMechanismProbeTests
{
    private static async Task<List<object?>> EvalAsync(string expr, object? result)
    {
        var store = new XdmDocumentStore();
        var engine = new QueryEngine(nodeProvider: store, documentResolver: store);
        var compiled = engine.Compile("declare variable $result external; " + expr);
        Assert.True(compiled.Success, $"compile failed: {string.Join("; ", compiled.Errors)}");

        var ctx = engine.CreateContext();
        ctx.SetExternalVariable("result", result);

        var items = new List<object?>();
        await foreach (var i in compiled.ExecutionPlan!.ExecuteAsync(ctx))
            items.Add(i);
        return items;
    }

    private static async Task<bool> IsTrueAsync(string expr, object? result)
    {
        var items = await EvalAsync(expr, result);
        return items.Count == 1 && items[0] is true;
    }

    [Fact]
    public async Task ScalarResult_IsVisibleToTheAssertion()
    {
        Assert.True(await IsTrueAsync("$result eq 42", 42));
        Assert.False(await IsTrueAsync("$result eq 43", 42));
    }

    [Fact]
    public void CompileFailureIsNotSilent()
    {
        // Guards the specific regression: an assertion that cannot compile must be visible
        // here rather than swallowed into a plain "false", which is what hid the inert
        // implementation. If this ever starts compiling, the "declare variable" prefix in
        // XqtsTestRunner.VerifyXPathAssertAsync is no longer required.
        var engine = new QueryEngine();
        var bare = engine.Compile("$result eq 42");
        Assert.False(bare.Success, "an undeclared $result must fail static analysis");
    }

    /// <summary>
    /// The engine's multi-item representation is <c>object?[]</c>; a <c>List&lt;object?&gt;</c>
    /// is a single item. Binding the raw list made <c>count($result)</c> return 1 on a
    /// three-item result and <c>$result[2]</c> return nothing, so every sequence-shaped
    /// assertion — most of the corpus — evaluated false. XqtsTestRunner.AsXdmSequence
    /// normalises before binding; these pin both halves of that.
    /// </summary>
    [Fact]
    public async Task SequenceResult_SupportsCountAndPositionalAccess()
    {
        var seq = new object?[] { 1, 2, 3, 4, 5 };
        Assert.True(await IsTrueAsync("count($result) = 5", seq));
        Assert.False(await IsTrueAsync("count($result) = 4", seq));
        Assert.True(await IsTrueAsync("$result[2] = 2", seq));
    }

    [Fact]
    public async Task RawListBindsAsASingleItem_WhichIsWhyNormalisationExists()
    {
        // Pins the trap rather than the fix: if this ever starts reporting 3, the engine
        // has learned to flatten List<object?> and AsXdmSequence can be simplified.
        var items = await EvalAsync("count($result)", new List<object?> { 1, 2, 3 });
        Assert.Equal("1", items[0]?.ToString());
    }
}
