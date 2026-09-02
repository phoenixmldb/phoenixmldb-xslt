using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:catch</c> must expose fn:error()'s description and error object verbatim through
/// <c>$err:description</c> and <c>$err:value</c> (XSLT 3.0 §13.3).
/// </summary>
/// <remarks>
/// <para>
/// Both were wrong in ways that could not fail loudly. <c>$err:value</c> was bound to a literal
/// null, so the error object vanished — and since fn:error()'s 1- and 2-argument forms have no
/// error object, an empty sequence is a legal answer that no test distinguished from the truth.
/// The XQuery engine's own try/catch had always bound it from <c>ErrorValue</c>; only the XSLT
/// side had the stub, the same producer-fixed/consumer-not shape seen elsewhere in this engine.
/// </para>
/// <para>
/// <c>$err:description</c> carried the engine's diagnostic rendering rather than the author's
/// string: EvaluateAsync re-throws with a "[module:line] " prefix and a trailing expression
/// snippet so a failing XPath can be located. Useful in a log, wrong in a variable a stylesheet
/// compares against the string it just passed to fn:error().
/// </para>
/// </remarks>
public class CatchErrorObjectTests
{
    private const string ErrNs = "http://www.w3.org/2005/xqt-errors";

    private static async Task<string> RunAsync(string raise, string report)
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" "
            + "xmlns:err=\"" + ErrNs + "\" version=\"3.0\" exclude-result-prefixes=\"#all\">"
            + "<xsl:template name=\"main\"><xsl:try>"
            + "<xsl:sequence select=\"" + raise + "\"/>"
            + "<xsl:catch><out>" + report + "</out></xsl:catch>"
            + "</xsl:try></xsl:template></xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    /// <summary>
    /// The three-argument form's error object reaches $err:value as a sequence, not as ().
    /// This is what XSpec's catch suite asserts with "?err?value treat as xs:string+".
    /// </summary>
    [Fact]
    public async Task ErrValue_CarriesTheErrorObjectSequence()
    {
        var result = await RunAsync(
            "error(xs:QName('my-code'), 'my description', ('a','b','c'))",
            "<xsl:value-of select=\"string-join($err:value, '|')\"/>");
        result.Should().Be("<out>a|b|c</out>");
    }

    /// <summary>
    /// Cardinality matters on its own: a treat-as of xs:string+ fails on an empty sequence even
    /// when the reported values look right, which is how this defect stayed invisible.
    /// </summary>
    [Fact]
    public async Task ErrValue_HasTheErrorObjectCardinality()
    {
        var result = await RunAsync(
            "error(xs:QName('my-code'), 'my description', ('a','b','c'))",
            "<xsl:value-of select=\"count($err:value)\"/>");
        result.Should().Be("<out>3</out>");
    }

    /// <summary>
    /// No error object supplied — $err:value must be the empty sequence, not a stray value.
    /// </summary>
    [Fact]
    public async Task ErrValue_IsEmptyWhenNoErrorObjectWasSupplied()
    {
        var result = await RunAsync(
            "error(xs:QName('my-code'), 'my description')",
            "<xsl:value-of select=\"count($err:value)\"/>");
        result.Should().Be("<out>0</out>");
    }

    /// <summary>
    /// The description is the author's string alone — no source-location prefix, no snippet.
    /// </summary>
    [Fact]
    public async Task ErrDescription_IsTheUndecoratedDescription()
    {
        var result = await RunAsync(
            "error(xs:QName('my-code'), 'my description', ('a','b','c'))",
            "<xsl:value-of select=\"$err:description\"/>");
        result.Should().Be("<out>my description</out>");
    }

    /// <summary>
    /// $err:code is unaffected by either change.
    /// </summary>
    [Fact]
    public async Task ErrCode_StillReportsTheRaisedCode()
    {
        var result = await RunAsync(
            "error(xs:QName('my-code'), 'my description')",
            "<xsl:value-of select=\"$err:code\"/>");
        result.Should().Be("<out>my-code</out>");
    }

    /// <summary>
    /// $err:line-number reports the line the failing expression sits on. fn:error() raises an
    /// XQueryException, which carries no location, so xsl:catch used to leave this empty and a
    /// stylesheet doing "$err:line-number treat as xs:integer" got a cardinality error.
    /// </summary>
    [Fact]
    public async Task ErrLineNumber_ReportsTheLineOfTheFailingExpression()
    {
        // Line 1 is the xsl:stylesheet element; the fn:error() call is on line 3.
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" "
            + "xmlns:err=\"" + ErrNs + "\" version=\"3.0\" exclude-result-prefixes=\"#all\">\n"
            + "<xsl:template name=\"main\"><xsl:try>\n"
            + "<xsl:sequence select=\"error(xs:QName('my-code'), 'my description')\"/>\n"
            + "<xsl:catch><out><xsl:value-of select=\"$err:line-number\"/></out></xsl:catch>\n"
            + "</xsl:try></xsl:template></xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        t.SetInitialTemplate("main");
        var result = await t.TransformAsync((string?)null);
        result.Should().Be("<out>3</out>");
    }

    /// <summary>
    /// $err:line-number is a single item, which is what "treat as xs:integer" requires.
    /// </summary>
    [Fact]
    public async Task ErrLineNumber_IsExactlyOneItem()
    {
        var result = await RunAsync(
            "error(xs:QName('my-code'), 'my description')",
            "<xsl:value-of select=\"count($err:line-number)\"/>");
        result.Should().Be("<out>1</out>");
    }

    /// <summary>
    /// EQName references resolve to the same bindings as the err: prefix.
    /// </summary>
    [Fact]
    public async Task ErrValue_IsAlsoReachableByEQName()
    {
        var result = await RunAsync(
            "error(xs:QName('my-code'), 'my description', ('a','b','c'))",
            "<xsl:value-of select=\"string-join($Q{" + ErrNs + "}value, '|')\"/>");
        result.Should().Be("<out>a|b|c</out>");
    }
}
