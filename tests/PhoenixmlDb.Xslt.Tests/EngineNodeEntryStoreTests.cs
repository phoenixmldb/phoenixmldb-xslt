using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="XsltTransformEngine.TransformAsync(Xdm.Nodes.XdmNode, XsltTransformOptions?)"/> gives the
/// transformation a store that builds nodes. It gave none, so a stylesheet that constructed nodes
/// failed through it: a path into a temporary tree ("An axis step ..."), an XPath element
/// constructor, the element of a <c>load-xquery-module</c> function (InvalidOperationException), a
/// path on <c>fn:analyze-string</c>, <c>fn:json-to-xml</c> (FOJS0001). An XPath text constructor made
/// a text node with the id 0. Each case is compared with the string entry point, which parses the
/// source into a store of its own.
/// </summary>
public sealed class EngineNodeEntryStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phoenixml-node-entry-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Source = "<r><i>1</i></r>";

    private string Sheet(string body) =>
        "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>"
        + $"<xsl:template match='/'><out>{body}</out></xsl:template></xsl:stylesheet>";

    private async Task<string> ViaNodeAsync(string body)
    {
        var stylesheet = new StylesheetParser(new XQueryExpressionParser()).Parse(Sheet(body), new Uri(Path.Combine(_dir, "s.xsl")));
        var store = new XQuery.XdmDocumentStore();
        var source = store.LoadFromString(Source, "urn:test:source");
        return await new XsltTransformEngine(stylesheet)
            .TransformAsync(source, new XsltTransformOptions { HasSourceDocument = true }).ConfigureAwait(true);
    }

    private async Task<string> ViaStringAsync(string body)
    {
        var stylesheet = new StylesheetParser(new XQueryExpressionParser()).Parse(Sheet(body), new Uri(Path.Combine(_dir, "s.xsl")));
        return await new XsltTransformEngine(stylesheet)
            .TransformAsync(Source, new XsltTransformOptions { HasSourceDocument = true }).ConfigureAwait(true);
    }

    // The expected value where the probe measured it; null: the string entry point is the reference.
    public static TheoryData<string, string?> Cases() => new()
    {
        { "<xsl:variable name='v'><a><b>x</b></a></xsl:variable><xsl:copy-of select='$v/a/b'/>", "<out><b>x</b></out>" },
        { "<xsl:copy-of select=\"&lt;q/&gt;\"/>", null },
        { "<xsl:value-of select=\"string(text { 'x' })\"/>", "<out>x</out>" },
        { "<xsl:evaluate xpath=\"'string(text { ''x'' })'\"/>", "<out>x</out>" },
        { "<xsl:value-of select=\"count(analyze-string('a1b', '\\d')/*)\"/>", "<out>3</out>" },
        { "<xsl:value-of select=\"count(json-to-xml('[1,2]')//*)\"/>", "<out>3</out>" },
        { "<xsl:copy-of select=\"parse-xml('&lt;p&gt;x&lt;/p&gt;')\"/>", null },
        // Guard: the source is read as before.
        { "<xsl:value-of select='count(//i)'/>|<xsl:value-of select='string(r/i)'/>", "<out>1|1</out>" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_stylesheet_that_constructs_nodes_works_through_the_node_entry_point(string body, string? expected)
    {
        var reference = await ViaStringAsync(body).ConfigureAwait(true);
        if (expected is not null)
            reference.Should().Be(expected, "the string entry point gives it");
        reference.Should().StartWith("<out>");
        (await ViaNodeAsync(body).ConfigureAwait(true)).Should().Be(reference);
    }

    [Fact]
    public async Task The_nodes_of_a_load_xquery_module_function_work_through_the_node_entry_point()
    {
        var module = Path.Combine(_dir, "m.xqm");
        await File.WriteAllTextAsync(module,
            "module namespace m = 'urn:m'; declare function m:nodes() { text { 'tx' }, <e a='1'>ex</e> };").ConfigureAwait(true);
        var body = $"<xsl:for-each select=\"load-xquery-module('urn:m', map {{ 'location-hints': '{new Uri(module).AbsoluteUri}' }})"
            + "?functions(QName('urn:m', 'nodes'))?0()\"><n k='{local-name(.)}' v='{string(.)}'/></xsl:for-each>";
        const string expected = "<out><n k=\"\" v=\"tx\"/><n k=\"e\" v=\"ex\"/></out>";
        (await ViaStringAsync(body).ConfigureAwait(true)).Should().Be(expected, "the string entry point gives it");
        (await ViaNodeAsync(body).ConfigureAwait(true)).Should().Be(expected);
    }
}
