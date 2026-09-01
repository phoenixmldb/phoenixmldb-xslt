using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>fn:transform</c>'s <c>initial-match-selection</c> holds real XDM items, and applies
/// templates to every one of them.
/// </summary>
/// <remarks>
/// The option used to be converted back into an XPath expression STRING, which cannot work: no
/// expression denotes an arbitrary existing node. The conversion's fallback was
/// <c>value.ToString()</c>, so a sequence became the CLR text <c>"System.Object[]"</c> — which
/// then parsed as XPath and failed with <c>mismatched input ']'</c>, the empty predicate of
/// <c>Object[]</c>.
///
/// <para>
/// A sequence is the normal case, not an edge case: XSpec routes its <c>x:context</c> to
/// <c>initial-match-selection</c> whenever it is not a single node, so every suite with more
/// than one context item took this path.
/// </para>
/// </remarks>
public class TransformMatchSelectionTests
{
    private static async Task<string> RunAsync(string selection)
    {
        var target =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\" "
            + "xmlns:t=\"urn:test:m\" exclude-result-prefixes=\"#all\">"
            + "<xsl:mode name=\"t:m\" on-no-match=\"fail\"/>"
            + "<xsl:template match=\"*\" mode=\"t:m\" as=\"item()*\">"
            + "<xsl:sequence select=\"'[' || name() || ':' || position() || '/' || last() || ']'\"/>"
            + "</xsl:template></xsl:stylesheet>";
        var escaped = System.Security.SecurityElement.Escape(
            target.Replace("'", "''", StringComparison.Ordinal))!;

        var driver =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\" "
            + "xmlns:t=\"urn:test:m\" exclude-result-prefixes=\"#all\">"
            + "<xsl:template name=\"main\">"
            + "<xsl:variable name=\"src\"><a/><b/><c/></xsl:variable>"
            + "<xsl:variable name=\"r\" select=\"transform(map { "
            + "'stylesheet-text': '" + escaped + "', "
            + "'delivery-format': 'raw', "
            + "'initial-mode': QName('urn:test:m', 't:m'), "
            + "'initial-match-selection': " + selection
            + " })\"/>"
            + "<out><xsl:value-of select=\"$r?output\" separator=\"\"/></out>"
            + "</xsl:template></xsl:stylesheet>";

        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(driver);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    /// <summary>
    /// A multi-node selection. Before the fix this raised XPST0003 rather than running at all.
    /// position()/last() are asserted too: they are what an item-by-item workaround would have
    /// silently got wrong, reporting 1/1 for every node.
    /// </summary>
    [Fact]
    public async Task InitialMatchSelection_AppliesTemplatesToEveryNodeInASequence()
    {
        var result = await RunAsync("$src/*");
        result.Should().Be("<out>[a:1/3][b:2/3][c:3/3]</out>");
    }

    /// <summary>A single node still works — it takes a different branch upstream.</summary>
    [Fact]
    public async Task InitialMatchSelection_AcceptsASingleNode()
    {
        var result = await RunAsync("$src/*[1]");
        result.Should().Be("<out>[a:1/1]</out>");
    }
}
