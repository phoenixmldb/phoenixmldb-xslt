using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A NAMED kind test in an <c>as</c> attribute must bind the declared node kind.
/// </summary>
/// <remarks>
/// <c>ParseSequenceType</c> matches unnamed spellings through an exact-string switch whose
/// default is <c>_ =&gt; ItemType.Item</c>. <c>element(name)</c> and <c>attribute(name)</c> each
/// have their own branch; <c>processing-instruction(target)</c> had none, so it silently became
/// <c>item()</c>.
///
/// <para>
/// That is not a harmless approximation. The global binder decides whether to build a real node
/// by testing for <c>ItemType.ProcessingInstruction</c>; an <c>Item</c> does not match, so the
/// binding fell to the legacy route and wrapped the node in a DOCUMENT. Both spellings are
/// asserted together so the named and unnamed forms cannot drift apart again.
/// </para>
/// </remarks>
public class NamedKindTestTypeTests
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

    [Fact]
    public async Task NamedAndUnnamedProcessingInstructionTypes_BothBindAProcessingInstruction()
    {
        var result = await RunAsync(
            "<xsl:variable as=\"processing-instruction()\" name=\"u\">"
            + "<xsl:processing-instruction name=\"p\">d</xsl:processing-instruction></xsl:variable>"
            + "<xsl:variable as=\"processing-instruction(p)\" name=\"n\">"
            + "<xsl:processing-instruction name=\"p\">d</xsl:processing-instruction></xsl:variable>"
            + "<xsl:template name=\"main\">"
            + "<out u=\"{$u instance of processing-instruction()}\""
            + " n=\"{$n instance of processing-instruction()}\""
            + " target=\"{name($n)}\" data=\"{string($n)}\"/></xsl:template>");

        result.Should().Be("<out u=\"true\" n=\"true\" target=\"p\" data=\"d\"/>");
    }

    /// <summary>The named forms that already worked, asserted so the trio stays consistent.</summary>
    [Fact]
    public async Task NamedElementAndAttributeTypes_StillBindTheirKinds()
    {
        var result = await RunAsync(
            "<xsl:variable as=\"element(e)\" name=\"e\"><e/></xsl:variable>"
            + "<xsl:variable as=\"attribute(a)\" name=\"a\"><xsl:attribute name=\"a\">v</xsl:attribute></xsl:variable>"
            + "<xsl:template name=\"main\">"
            + "<out e=\"{$e instance of element()}\" a=\"{$a instance of attribute()}\"/></xsl:template>");

        result.Should().Be("<out e=\"true\" a=\"true\"/>");
    }
}
