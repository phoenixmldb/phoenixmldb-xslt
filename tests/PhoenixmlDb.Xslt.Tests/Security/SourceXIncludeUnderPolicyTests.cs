using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

#pragma warning disable CA2007 // xUnit runs tests without a synchronization context

namespace PhoenixmlDb.Xslt.Tests.Security;

/// <summary>
/// With XInclude enabled for the source document, what the document includes is read under
/// the transformation's resource policy. It used to be opened by a resolver that knows no
/// policy: a source document could pull any file into the transformation.
/// </summary>
public sealed class SourceXIncludeUnderPolicyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-source-xinclude").FullName;
    private readonly string _allowed;
    private readonly string _secret;

    public SourceXIncludeUnderPolicyTests()
    {
        _allowed = Directory.CreateDirectory(Path.Combine(_dir, "allowed")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.xml"), "<s>outside-content</s>");
        File.WriteAllText(Path.Combine(_allowed, "part.xml"), "<p>inside-content</p>");
        _secret = new Uri(Path.Combine(outside, "secret.xml")).AbsoluteUri;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> RunAsync(string href, bool withPolicy)
    {
        var transformer = new XsltTransformer
        {
            ResourcePolicy = withPolicy ? ResourcePolicy.CreateBuilder().AllowReadFrom("file", pathPrefix: _allowed).Build() : null,
        };
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template match="/"><out><xsl:value-of select="."/></out></xsl:template>
            </xsl:stylesheet>
            """, new Uri("urn:test:main.xsl"));
        transformer.EnableXInclude();
        transformer.SetSourceDocumentUri(new Uri(Path.Combine(_allowed, "source.xml")));
        try
        {
            return (await transformer.TransformAsync($"<r xmlns:xi='http://www.w3.org/2001/XInclude'><xi:include href='{href}'/></r>")).Trim();
        }
#pragma warning disable CA1031 // any failure is the outcome under test
        catch (Exception e)
#pragma warning restore CA1031
        {
            return "error: " + e.Message;
        }
    }

    [Fact]
    public async Task An_include_of_a_file_the_policy_refuses_is_not_read()
        => (await RunAsync(_secret, withPolicy: true)).Should().NotContain("outside-content");

    [Fact]
    public async Task An_include_of_a_file_the_policy_allows_is_read()
        => (await RunAsync("part.xml", withPolicy: true)).Should().Be("<out>inside-content</out>");

    [Fact]
    public async Task With_no_policy_the_include_is_read_as_before()
        => (await RunAsync(_secret, withPolicy: false)).Should().Be("<out>outside-content</out>");
}
