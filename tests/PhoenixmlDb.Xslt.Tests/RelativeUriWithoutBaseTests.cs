using AwesomeAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A relative URI with no base URI to resolve it against names nothing. In a stylesheet loaded
/// from a string, unparsed-text, unparsed-text-lines and unparsed-text-available read the file
/// of that name in the process's working directory, with or without a resource policy; under
/// a policy doc, doc-available, document and json-doc did too, and without one doc-available
/// threw InvalidOperationException. Streaming xsl:merge opened the source there. Now each
/// refuses it (an availability function answers false) with the function's own error, which
/// does not name the working directory. What the host supplies under the name is served.
/// </summary>
public sealed class RelativeUriWithoutBaseTests : IDisposable
{
    private readonly string _stem = "phx-xnobase-" + Guid.NewGuid().ToString("N");
    private readonly string _cwd = Directory.GetCurrentDirectory();

    public RelativeUriWithoutBaseTests()
    {
        File.WriteAllText(Path.Combine(_cwd, _stem + ".xml"), "<r><i k='1'>7</i></r>");
        File.WriteAllText(Path.Combine(_cwd, _stem + ".txt"), "7");
        File.WriteAllText(Path.Combine(_cwd, _stem + ".json"), """{ "n": "7" }""");
        File.WriteAllText(Path.Combine(_cwd, _stem + "-m.xml"), "<r><r k='1'>7</r></r>");
    }

    public void Dispose()
    {
        foreach (var ext in new[] { ".xml", ".txt", ".json", "-m.xml" })
        {
            try { File.Delete(Path.Combine(_cwd, _stem + ext)); } catch (IOException) { }
        }
    }

    private static ResourcePolicy? Policy(string kind) => kind switch
    {
        "none" => null,
        "allow-file" => ResourcePolicy.CreateBuilder().AllowReadFrom("file").Build(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Sheet(string select) =>
        "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'><xsl:output method='text'/>"
        + "<xsl:template match='/'><xsl:value-of select=\"" + select + "\"/></xsl:template></xsl:stylesheet>";

    private async Task<string> RunAsync(ResourcePolicy? policy, string stylesheet, Uri? baseUri = null)
    {
        var transformer = new XsltTransformer { ResourcePolicy = policy };
        await transformer.LoadStylesheetAsync(stylesheet, baseUri);
        return (await transformer.TransformAsync("<x/>")).Trim();
    }

    private string Expr(string expression) => expression.Replace("{0}", _stem, StringComparison.Ordinal);

    [Theory]
    [InlineData("none", "string(doc('{0}.xml'))", "FODC0002")]
    [InlineData("none", "string(document('{0}.xml'))", "FODC0005")]
    [InlineData("none", "unparsed-text('{0}.txt')", "FOUT1170")]
    [InlineData("none", "string-join(unparsed-text-lines('{0}.txt'))", "FOUT1170")]
    [InlineData("none", "string(json-doc('{0}.json')?n)", "FOUT1170")]
    [InlineData("allow-file", "string(doc('{0}.xml'))", "FODC0002")]
    [InlineData("allow-file", "string(document('{0}.xml'))", "FODC0005")]
    [InlineData("allow-file", "unparsed-text('{0}.txt')", "FOUT1170")]
    [InlineData("allow-file", "string-join(unparsed-text-lines('{0}.txt'))", "FOUT1170")]
    [InlineData("allow-file", "string(json-doc('{0}.json')?n)", "FOUT1170")]
    public async Task The_function_refuses_it(string policy, string expression, string code)
    {
        var act = () => RunAsync(Policy(policy), Sheet(Expr(expression)));

        var error = (await act.Should().ThrowAsync<Exception>()).Which;
        CodeOf(error).Should().Be(code);
        error.ToString().Should().NotContain(_cwd);
    }

    // An XPath error carries its code in ErrorCode; an XSLT error starts its message with it.
    private static string CodeOf(Exception e) => e switch
    {
        PhoenixmlDb.XQuery.Execution.XQueryRuntimeException x => x.ErrorCode,
        PhoenixmlDb.XQuery.Functions.XQueryException x => x.ErrorCode,
        _ => e.Message.Split(':')[0],
    };

    [Theory]
    [InlineData("none", "doc-available('{0}.xml')")]
    [InlineData("none", "unparsed-text-available('{0}.txt')")]
    [InlineData("allow-file", "doc-available('{0}.xml')")]
    [InlineData("allow-file", "unparsed-text-available('{0}.txt')")]
    public async Task The_availability_function_answers_false(string policy, string expression)
        => (await RunAsync(Policy(policy), Sheet(Expr(expression)))).Should().Be("false");

    [Theory]
    [InlineData("none")]
    [InlineData("allow-file")]
    public async Task With_a_base_uri_the_same_names_are_read(string policy)
    {
        var select = Expr("concat(doc('{0}.xml'), document('{0}.xml'), unparsed-text('{0}.txt'), json-doc('{0}.json')?n, doc-available('{0}.xml'), unparsed-text-available('{0}.txt'))");

        (await RunAsync(Policy(policy), Sheet(select), new Uri(Path.Combine(_cwd, "main.xsl")))).Should().Be("7777truetrue");
    }

    // select='*' streams (XsltMergeStreaming.CanStreamMerge); select='r/i' does not. The
    // streamed source is <r><r k='1'>7</r></r>, so '*' gives "7" whether it is taken from the
    // document node or, as the streaming reader takes it, from inside the document element.
    private string MergeSheet(bool streamed = true, string? source = null) =>
        "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'><xsl:output method='text'/>"
        + "<xsl:template match='/'><xsl:merge>"
        + $"<xsl:merge-source for-each-source=\"'{source ?? (streamed ? _stem + "-m.xml" : _stem + ".xml")}'\" streamable='{(streamed ? "yes" : "no")}' select='{(streamed ? "*" : "r/i")}'><xsl:merge-key select='@k'/></xsl:merge-source>"
        + "<xsl:merge-action><xsl:value-of select='current-merge-group()'/></xsl:merge-action>"
        + "</xsl:merge></xsl:template></xsl:stylesheet>";

    [Theory]
    [InlineData("none", true)]
    [InlineData("allow-file", true)]
    [InlineData("none", false)]
    [InlineData("allow-file", false)]
    public async Task A_merge_source_is_refused(string policy, bool streamed)
    {
        var act = () => RunAsync(Policy(policy), MergeSheet(streamed));

        var error = (await act.Should().ThrowAsync<Exception>()).Which;
        CodeOf(error).Should().Be("FODC0002");
        error.ToString().Should().NotContain(_cwd);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_merge_source_is_read_with_a_base_uri(bool streamed)
        => (await RunAsync(null, MergeSheet(streamed), new Uri(Path.Combine(_cwd, "main.xsl")))).Should().Be("7");

    private sealed class Supplier : ResourceResolverBase
    {
        public List<string> Asked { get; } = [];

        public override ResourceContent? ResolveContent(ResourceRequest request)
        {
            Asked.Add(request.Location);
            return request.Location switch
            {
                "orders.xml" => new ResourceContent("<r><i k='1'>supplied</i></r>", new Uri("urn:host:orders.xml")),
                "orders-m.xml" => new ResourceContent("<r><r k='1'>supplied</r></r>", new Uri("urn:host:orders-m.xml")),
                "orders.txt" => new ResourceContent("supplied", new Uri("urn:host:orders.txt")),
                "orders.json" => new ResourceContent("""{ "n": "supplied" }""", new Uri("urn:host:orders.json")),
                _ => null,
            };
        }
    }

    [Fact]
    public async Task Content_the_host_resolver_supplies_under_the_name_is_served()
    {
        var host = new Supplier();
        var policy = ResourcePolicy.CreateBuilder().WithResourceResolver(host).Build();

        (await RunAsync(policy, Sheet("concat(doc('orders.xml'), document('orders.xml'), unparsed-text('orders.txt'), json-doc('orders.json')?n)")))
            .Should().Be("suppliedsuppliedsuppliedsupplied");
    }

    [Fact]
    public async Task A_streamed_merge_source_the_host_supplies_is_read_and_the_host_is_asked_by_the_name()
    {
        var host = new Supplier();
        var policy = ResourcePolicy.CreateBuilder().WithResourceResolver(host).Build();
        (await RunAsync(policy, MergeSheet(source: "orders-m.xml"))).Should().Be("supplied");
        host.Asked.Should().Contain("orders-m.xml").And.NotContain(a => a.Contains(_cwd, StringComparison.Ordinal));
    }
}
