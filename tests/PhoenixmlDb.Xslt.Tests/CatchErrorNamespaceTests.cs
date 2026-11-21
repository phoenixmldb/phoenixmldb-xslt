using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An xsl:catch name matches an error by local name AND namespace URI (XSLT 3.0 §8.3). A standard
/// error was caught by any name with the prefix <c>err</c> or with any namespace at all, so
/// <c>errors="q:FORG0001"</c> with <c>q</c> bound to another URI caught <c>err:FORG0001</c>; an error
/// fn:error raised in a namespace was caught only by a name in no namespace; <c>err:*</c> caught
/// nothing; and an xsl:message error-code in another namespace could not be caught by name
/// (BUGS.md #135; Lucas, 3.3.0).
/// </summary>
public class CatchErrorNamespaceTests
{
    private const string ErrUri = "http://www.w3.org/2005/xqt-errors";
    private const string BadDate = """<xsl:value-of select="xs:date('x')"/>""";
    private const string UserError = """<xsl:value-of select="error(QName('urn:u', 'e'))"/>""";

    private static async Task<string> Run(string namespaces, string body, string errors)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" {{namespaces}}>
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:try>{{body}}<xsl:catch errors="{{errors}}">caught <xsl:value-of
                    select="namespace-uri-from-QName($Q{{{ErrUri}}}code), local-name-from-QName($Q{{{ErrUri}}}code)"/></xsl:catch>
                  <xsl:catch>other</xsl:catch></xsl:try>
              </xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync("<d/>")).Trim();
    }

    [Theory]
    // a name in another namespace does not catch a standard error, whatever its prefix
    [InlineData("""xmlns:q="urn:other" """, BadDate, "q:FORG0001", "other")]
    [InlineData("""xmlns:err="urn:other" """, BadDate, "err:FORG0001", "other")]
    [InlineData("", BadDate, "FORG0001", "other")]
    // the standard namespace under any prefix, any-namespace and namespace wildcards
    [InlineData($"""xmlns:e="{ErrUri}" """, BadDate, "e:FORG0001", $"caught {ErrUri} FORG0001")]
    [InlineData($"""xmlns:err="{ErrUri}" """, BadDate, "err:FORG0001", $"caught {ErrUri} FORG0001")]
    [InlineData("", BadDate, "*:FORG0001", $"caught {ErrUri} FORG0001")]
    [InlineData($"""xmlns:err="{ErrUri}" """, BadDate, "err:*", $"caught {ErrUri} FORG0001")]
    [InlineData("", BadDate, $"Q{{{ErrUri}}}*", $"caught {ErrUri} FORG0001")]
    [InlineData("", BadDate, "*", $"caught {ErrUri} FORG0001")]
    // an error fn:error raised in a namespace, and one in no namespace
    [InlineData("""xmlns:u="urn:u" """, UserError, "u:e", "caught urn:u e")]
    [InlineData("", UserError, "e", "other")]
    [InlineData("", """<xsl:value-of select="error(QName('', 'e'))"/>""", "e", "caught  e")]
    // xsl:message terminate="yes" with an error code in another namespace
    [InlineData("""xmlns:u="urn:u" """, """<xsl:message terminate="yes" error-code="u:m">m</xsl:message>""", "u:m", "caught urn:u m")]
    public async Task A_catch_name_matches_by_namespace(string namespaces, string body, string errors, string expected)
        => (await Run(namespaces, body, errors)).Should().Be(expected);
}
