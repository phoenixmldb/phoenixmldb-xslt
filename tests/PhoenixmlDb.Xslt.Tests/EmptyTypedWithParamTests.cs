using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A typed <c>xsl:with-param</c> whose sequence-constructor body produces nothing must bind the
/// empty sequence, exactly as the equivalent <c>xsl:variable</c> already did.
/// </summary>
/// <remarks>
/// <para>
/// The engine represents the empty sequence two ways — a null and a zero-length array — and
/// <c>CoerceToType</c> handled only the null. Its per-item branch was guarded on
/// <c>arr.Length &gt; 1</c>, so a zero-length array fell through to the scalar arms, where
/// <c>ItemType.String =&gt; StringValueOf(array)</c> produced a single zero-length string. "No
/// items" silently became "one empty item".
/// </para>
/// <para>
/// Which representation a caller sees depends on how the parameter is written, not on what it
/// means: <c>select="()"</c> returns null and was correct, while a sequence-constructor body
/// that yields nothing returns Array.Empty and was not. That is why the same logical value
/// behaved differently depending on spelling.
/// </para>
/// <para>
/// Found via XSpec's catch suites. XSpec passes accumulated variable names through
/// <c>&lt;xsl:with-param as="xs:string*"&gt;</c> with a constructor body; the phantom empty
/// string became a generated <c>&lt;xsl:param name=""&gt;</c> and
/// <c>&lt;xsl:with-param name="" select="$"/&gt;</c>, and the bare <c>$</c> then failed to
/// parse — an XPST0003 several steps removed from the actual defect.
/// </para>
/// </remarks>
public class EmptyTypedWithParamTests
{
    private static async Task<string> RunAsync(string callerParam, string calleeParam)
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" version=\"3.0\" "
            + "exclude-result-prefixes=\"#all\">"
            + "<xsl:template name=\"main\"><xsl:call-template name=\"callee\">"
            + callerParam
            + "</xsl:call-template></xsl:template>"
            + "<xsl:template name=\"callee\"><xsl:param name=\"p\"" + calleeParam + "/>"
            + "<r><xsl:value-of select=\"count($p)\"/></r></xsl:template></xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    private const string EmptyBody =
        "<xsl:with-param name=\"p\" as=\"xs:string*\">"
        + "<xsl:if test=\"false()\"><xsl:sequence select=\"'x'\"/></xsl:if>"
        + "<xsl:sequence select=\"()\"/></xsl:with-param>";

    /// <summary>The defect: a constructor body yielding nothing bound one empty string.</summary>
    [Fact]
    public async Task TypedWithParam_WithEmptyConstructorBody_BindsEmptySequence()
    {
        var result = await RunAsync(EmptyBody, " as=\"xs:string*\"");
        result.Should().Be("<r>0</r>");
    }

    /// <summary>
    /// The same value written select="()" always worked; both spellings must now agree.
    /// </summary>
    [Fact]
    public async Task TypedWithParam_WithSelectEmptySequence_BindsEmptySequence()
    {
        var result = await RunAsync(
            "<xsl:with-param name=\"p\" as=\"xs:string*\" select=\"()\"/>", " as=\"xs:string*\"");
        result.Should().Be("<r>0</r>");
    }

    /// <summary>
    /// The coercion is what manufactured the item, so an untyped callee always read 0. Pinning
    /// it keeps the two paths from diverging again.
    /// </summary>
    [Fact]
    public async Task TypedWithParam_WithEmptyConstructorBody_ReadsEmptyAtAnUntypedCallee()
    {
        var result = await RunAsync(EmptyBody, "");
        result.Should().Be("<r>0</r>");
    }

    /// <summary>A non-empty constructor body must still deliver every item.</summary>
    [Fact]
    public async Task TypedWithParam_WithNonEmptyConstructorBody_BindsEveryItem()
    {
        var result = await RunAsync(
            "<xsl:with-param name=\"p\" as=\"xs:string*\">"
            + "<xsl:sequence select=\"'a'\"/><xsl:sequence select=\"('b','c')\"/>"
            + "</xsl:with-param>",
            " as=\"xs:string*\"");
        result.Should().Be("<r>3</r>");
    }

    /// <summary>
    /// A single-item body is the boundary the old Length &gt; 1 guard also excluded; it must
    /// still bind exactly one item.
    /// </summary>
    [Fact]
    public async Task TypedWithParam_WithSingleItemConstructorBody_BindsOneItem()
    {
        var result = await RunAsync(
            "<xsl:with-param name=\"p\" as=\"xs:string*\"><xsl:sequence select=\"'only'\"/>"
            + "</xsl:with-param>",
            " as=\"xs:string*\"");
        result.Should().Be("<r>1</r>");
    }

    /// <summary>
    /// The xsl:variable twin, which already behaved correctly — kept as the reference the
    /// with-param path is required to match.
    /// </summary>
    [Fact]
    public async Task TypedVariable_WithEmptyConstructorBody_StillBindsEmptySequence()
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" version=\"3.0\" "
            + "exclude-result-prefixes=\"#all\"><xsl:template name=\"main\">"
            + "<xsl:variable name=\"v\" as=\"xs:string*\">"
            + "<xsl:if test=\"false()\"><xsl:sequence select=\"'x'\"/></xsl:if>"
            + "<xsl:sequence select=\"()\"/></xsl:variable>"
            + "<r><xsl:value-of select=\"count($v)\"/></r></xsl:template></xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        t.SetInitialTemplate("main");
        var result = await t.TransformAsync((string?)null);
        result.Should().Be("<r>0</r>");
    }
}
