using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A template with as="text()" returning "&lt;" into a variable yields a text node "&lt;", not
/// the four characters of its serialized form "&amp;lt;". The captured body output is serialized
/// text, and a chunk with no markup was kept as-is, entity references included (W3C doe-0183/0186,
/// where disable-output-escaping is ignored in the temporary tree).
/// </summary>
public sealed class TypedTemplateTextInVariableTests
{
    private static async Task<string> RunAsync(string doe)
    {
        var xsl = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
                  "<xsl:output method='xml' omit-xml-declaration='yes'/>" +
                  "<xsl:template name='xsl:initial-template'><xsl:variable name='x'><xsl:apply-templates select=\"'&lt;&amp;'\" mode='m'/></xsl:variable>" +
                  "<out len='{string-length($x)}'><xsl:copy-of select='$x'/></out></xsl:template>" +
                  "<xsl:template match='.' mode='m' as='text()'><xsl:value-of select='.' disable-output-escaping='" + doe + "'/></xsl:template>" +
                  "</xsl:stylesheet>";
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("yes")] // d-o-e is ignored when the text goes into a temporary tree
    public async Task The_variable_holds_the_characters_not_their_escapes(string doe)
        => (await RunAsync(doe)).Should().Be("<out len=\"2\">&lt;&amp;</out>");
}
