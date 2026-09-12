using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An xsl:element carrying an attribute this processor must not accept at all is told about that
/// (XTSE1660) rather than about its missing name: the name is the mistake the author can fix by
/// typing more, the schema-aware attribute is the one they cannot (W3C error-1660b/c).
/// </summary>
public sealed class ElementSchemaAwareOrderTests
{
    private static async Task AssertErrorAsync(string body, string code)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var act = async () =>
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(ss);
            return await t.TransformAsync("<doc/>");
        };
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain(code);
    }

    [Theory]
    [InlineData("""<xsl:element type="xs:untyped"><x/></xsl:element>""")]
    [InlineData("""<xsl:element validation="strict"><x/></xsl:element>""")]
    public Task AnElementWithASchemaAwareAttributeAndNoName_IsXTSE1660(string body)
        => AssertErrorAsync(body, "XTSE1660");

    [Theory]
    [InlineData("""<xsl:element name="e" type="xs:untyped"/>""")]
    [InlineData("""<xsl:element name="e" validation="strict"/>""")]
    public Task ANamedElementWithASchemaAwareAttribute_IsStillXTSE1660(string body)
        => AssertErrorAsync(body, "XTSE1660");

    [Fact]
    public Task AnElementWithNoNameAndNothingElse_IsStillXTSE0010()
        => AssertErrorAsync("<xsl:element><x/></xsl:element>", "XTSE0010");
}
