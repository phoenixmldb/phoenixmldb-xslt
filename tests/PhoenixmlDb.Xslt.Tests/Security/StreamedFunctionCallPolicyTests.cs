using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

#pragma warning disable CA2007 // xUnit runs tests without a synchronization context

namespace PhoenixmlDb.Xslt.Tests.Security;

/// <summary>
/// Inside a streamed xsl:source-document, a function call whose arguments the streaming pass
/// already holds (literals among them) is evaluated outside the compiled plan, in a query
/// context made for that one call. The context carried no resource policy, so a function that
/// reads a resource read it with none: fn:json-doc opened a file the policy refuses.
/// </summary>
public sealed class StreamedFunctionCallPolicyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-streamed-call").FullName;
    private readonly string _allowed;
    private readonly string _outside;

    public StreamedFunctionCallPolicyTests()
    {
        _allowed = Directory.CreateDirectory(Path.Combine(_dir, "allowed")).FullName;
        File.WriteAllText(Path.Combine(_allowed, "books.xml"), "<r><i>a b</i><i>2</i></r>");
        File.WriteAllText(Path.Combine(_allowed, "inside.json"), "\"allowed-content\"");
        var outside = Path.Combine(_dir, "outside.json");
        File.WriteAllText(outside, "\"refused-content\"");
        _outside = new Uri(outside).AbsoluteUri;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> RunAsync(string body, bool withPolicy = true)
    {
        var transformer = new XsltTransformer
        {
            ResourcePolicy = withPolicy ? ResourcePolicy.CreateBuilder().AllowReadFrom("file", pathPrefix: _allowed).Build() : null,
        };
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="books.xml"><out>{body}</out></xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """, new Uri(_allowed + "/"));
        transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        try
        {
            return await transformer.TransformAsync((string?)null);
        }
#pragma warning disable CA1031 // any failure is the outcome under test
        catch (Exception e)
#pragma warning restore CA1031
        {
            return "error: " + e.Message;
        }
    }

    // The xsl:element name is what puts the streaming pass in the state where the call
    // beside it is evaluated outside the plan.
    private string Reading(string location)
        => "<xsl:element name=\"{translate(head(//i), ' ', '_')}\"><xsl:value-of select=\"json-doc('" + location + "')\"/></xsl:element>";

    [Fact]
    public async Task A_function_called_in_a_streamed_document_reads_only_what_the_policy_allows()
    {
        var result = await RunAsync(Reading(_outside));

        result.Should().NotContain("refused-content");
        result.Should().StartWith("error:").And.Contain("denied");
    }

    [Fact]
    public async Task What_the_policy_allows_is_still_read()
        => (await RunAsync(Reading("inside.json"))).Should().Contain("<a_b>allowed-content</a_b>");

    [Fact]
    public async Task With_no_policy_the_file_is_read_as_before()
        => (await RunAsync(Reading(_outside), withPolicy: false)).Should().Contain("<a_b>refused-content</a_b>");
}
