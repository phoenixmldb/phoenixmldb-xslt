using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Text nodes carried in a sequence must be matchable by <c>xsl:apply-templates</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>TextNodeItem</c> is an internal marker: a bare string that lets the sequence accumulator
/// distinguish a text node from an atomic string, which XSLT 3.0 §5.7.2 requires because
/// adjacent text nodes merge without a separator while atomic values join with spaces. It is
/// cheaper than a node precisely because it has no identity, parent or store — and every one of
/// those is needed by pattern matching.
/// </para>
/// <para>
/// So a sequence holding one matched neither <c>text()</c> nor even <c>node()</c>, and a mode
/// declaring <c>on-no-match="fail"</c> raised XTDE0555 for a node the stylesheet plainly
/// handles. XSpec's <c>local:report-node</c> mode is exactly that, so any suite whose result
/// contained a text node died outright.
/// </para>
/// </remarks>
public class TextNodeItemMatchingTests
{
    private static async Task<string> RunAsync(string body)
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" version=\"3.0\" "
            + "exclude-result-prefixes=\"#all\">" + body + "</xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    /// <summary>
    /// The shape XSpec uses: a result sequence of text nodes fed to a mode whose
    /// <c>on-no-match</c> is <c>fail</c>, with a <c>node()</c> template that must match them.
    /// </summary>
    [Fact]
    public async Task TextNodesInASequence_AreMatchedByApplyTemplates()
    {
        var result = await RunAsync(
            "<xsl:mode name=\"m\" on-no-match=\"fail\"/>"
            // Emits an ELEMENT per match rather than text, so the assertion tests matching and
            // not the §5.7.2 space-separation of adjacent atomic results, which is unrelated.
            + "<xsl:template match=\"node()\" mode=\"m\" as=\"item()*\">"
            + "<hit><xsl:value-of select=\"string(.)\"/></hit></xsl:template>"
            + "<xsl:template name=\"mk\" as=\"item()*\">"
            + "<xsl:text>one</xsl:text><xsl:text>two</xsl:text></xsl:template>"
            + "<xsl:template name=\"main\">"
            + "<xsl:variable name=\"r\" as=\"item()*\"><xsl:call-template name=\"mk\"/></xsl:variable>"
            + "<out><xsl:apply-templates select=\"$r\" mode=\"m\"/></out>"
            + "</xsl:template>");

        result.Should().Be("<out><hit>one</hit><hit>two</hit></out>");
    }

    /// <summary>
    /// The materialized item must be a genuine text node, not merely something that matches:
    /// <c>text()</c> selects it and it reports the right node kind.
    /// </summary>
    [Fact]
    public async Task AMaterializedTextNode_IsATextNode()
    {
        var result = await RunAsync(
            "<xsl:mode name=\"m\" on-no-match=\"fail\"/>"
            + "<xsl:template match=\"text()\" mode=\"m\" as=\"item()*\">"
            + "<hit kind=\"{node-name(.) => count()}\" str=\"{string(.)}\"/></xsl:template>"
            + "<xsl:template name=\"main\">"
            + "<xsl:variable name=\"r\" as=\"item()*\"><xsl:text>abc</xsl:text></xsl:variable>"
            + "<out><xsl:apply-templates select=\"$r\" mode=\"m\"/></out>"
            + "</xsl:template>");

        result.Should().Be("<out><hit kind=\"0\" str=\"abc\"/></out>");
    }
}
