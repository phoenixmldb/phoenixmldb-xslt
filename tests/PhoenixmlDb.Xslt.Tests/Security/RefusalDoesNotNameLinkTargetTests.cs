using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

#pragma warning disable CA2007 // xUnit runs tests without a synchronization context

namespace PhoenixmlDb.Xslt.Tests.Security;

/// <summary>
/// A stylesheet can catch a refusal and read its description. The description names what was
/// asked for and not the canonical path, which would give away where a symbolic link leads.
/// </summary>
public sealed class RefusalDoesNotNameLinkTargetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-xslt-link").FullName;
    private readonly string _link;

    public RefusalDoesNotNameLinkTargetTests()
    {
        var target = Directory.CreateDirectory(Path.Combine(_dir, "where-the-link-leads")).FullName;
        _link = new Uri(Path.Combine(_dir, "link")).AbsoluteUri;
        Directory.CreateSymbolicLink(Path.Combine(_dir, "link"), target);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("<xsl:copy-of select=\"doc('{0}/x.xml')\"/>")]
    [InlineData("<xsl:value-of select=\"unparsed-text('{0}/x.txt')\"/>")]
    [InlineData("<xsl:copy-of select=\"collection('{0}')\"/>")]
    [InlineData("<xsl:source-document href=\"{0}/x.xml\"><xsl:copy-of select=\".\"/></xsl:source-document>")]
    [InlineData("<xsl:source-document href=\"{0}/x.xml\" streamable=\"yes\"><xsl:copy-of select=\".\"/></xsl:source-document>")]
    [InlineData("<xsl:result-document href=\"{0}/out.xml\"><a/></xsl:result-document>")]
    [InlineData("<xsl:merge><xsl:merge-source for-each-source=\"'{0}/x.xml'\" streamable=\"yes\" select=\"r/i\"><xsl:merge-key select=\"@k\"/></xsl:merge-source><xsl:merge-action><m/></xsl:merge-action></xsl:merge>")]
    [InlineData("<xsl:merge><xsl:merge-source for-each-source=\"'{0}/x.xml'\" select=\"r/i\"><xsl:merge-key select=\"@k\"/></xsl:merge-source><xsl:merge-action><m/></xsl:merge-action></xsl:merge>")]
    public async Task A_stylesheet_cannot_read_a_link_target_from_the_refusal(string instruction)
    {
        var body = string.Format(System.Globalization.CultureInfo.InvariantCulture, instruction, _link);
        var transformer = new XsltTransformer { ResourcePolicy = ResourcePolicy.CreateBuilder().Build() };
        await transformer.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:err="http://www.w3.org/2005/xqt-errors">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template match="/">
                <out><xsl:try>{{body}}<xsl:catch>caught: <xsl:value-of select="$err:description"/></xsl:catch></xsl:try></out>
              </xsl:template>
            </xsl:stylesheet>
            """, new Uri("urn:test:main.xsl"));

        string result;
        try
        {
            result = await transformer.TransformAsync("<x/>");
        }
        catch (Exception e) when (e is ResourceAccessDeniedException || e.GetType().Name.Contains("Xslt", StringComparison.Ordinal))
        {
            result = "caught: " + e.Message;
        }

        result.Should().Contain("caught").And.NotContain("where-the-link-leads");
    }
}
