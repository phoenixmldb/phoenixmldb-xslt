using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A string returned through <c>fn:transform</c> with <c>delivery-format='raw'</c> is that
/// string. The raw path returned a constructed result tree as serialized text and a typed
/// xs:string as text too, and the caller told them apart by looking for '&lt;': a string VALUE
/// holding markup was parsed into nodes. The text of an XML file came back as two whitespace
/// text items, and <c>'&lt;a&gt;hello&lt;/a&gt;'</c> as an element (xslt#314, an XSpec suite
/// run with run-as="external").
/// </summary>
public class TransformStringResultTests
{
    private const string Mirror = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
            xmlns:m="urn:m" exclude-result-prefixes="#all">
          <xsl:function name="m:mirror" as="item()*" visibility="public">
            <xsl:param name="p" as="item()*"/>
            <xsl:sequence select="$p"/>
          </xsl:function>
          <xsl:template name="built"><out><in/></out></xsl:template>
          <xsl:template name="text">no markup</xsl:template>
        </xsl:stylesheet>
        """;

    private const string Describe =
        "string-join($r ! ((if (. instance of xs:string) then 'string' else if (. instance of element()) then 'element' else 'other') || ':' || string(.)), '|')";

    private static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>fn:transform called from a query.</summary>
    private static async Task<string> FromQuery(string options)
    {
        PhoenixmlDb.XQuery.Functions.TransformFunction.Provider ??= new XsltTransformProvider();
        var xq = $$"""
            let $r := transform(map { 'stylesheet-text': {{Quote(Mirror)}}, 'delivery-format': 'raw', {{options}} })?output
            return {{Describe}}
            """;
        return await new PhoenixmlDb.XQuery.XQueryFacade().EvaluateAsync(xq);
    }

    /// <summary>fn:transform called from a stylesheet, as XSpec's external runs do.</summary>
    private static async Task<string> FromStylesheet(string options)
    {
        var outer = $$"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:param name="sheet" as="xs:string"/>
              <xsl:template name="xsl:initial-template">
                <xsl:variable name="r" select="transform(map { 'stylesheet-text': $sheet, 'delivery-format': 'raw', {{options.Replace("<", "&lt;", StringComparison.Ordinal)}} })?output"/>
                <xsl:value-of select="{{Describe}}"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(outer);
        transformer.SetParameter("sheet", Mirror);
        return await transformer.TransformAsync((string?)null);
    }

    private static string Call(string argument) =>
        $"'initial-function': QName('urn:m', 'mirror'), 'function-params': [{argument}]";

    [Theory]
    [InlineData("'<a>hello</a>'", "string:<a>hello</a>")]
    [InlineData("'x <a>hello</a> y'", "string:x <a>hello</a> y")]
    [InlineData("'<!-- c --><a/>'", "string:<!-- c --><a/>")]
    [InlineData("'not < xml'", "string:not < xml")]
    [InlineData("('<a/>', '<b/>')", "string:<a/>|string:<b/>")]
    [InlineData("'plain'", "string:plain")]
    public async Task A_string_holding_markup_comes_back_as_that_string(string argument, string expected)
    {
        (await FromQuery(Call(argument))).Should().Be(expected);
        (await FromStylesheet(Call(argument))).Should().Be(expected);
    }

    [Fact]
    public async Task A_constructed_result_tree_still_comes_back_as_nodes()
    {
        const string options = "'initial-template': QName('', 'built')";
        (await FromQuery(options)).Should().Be("element:");
        (await FromStylesheet(options)).Should().Be("element:");
    }

    [Fact]
    public async Task Constructed_text_with_no_markup_still_comes_back_as_a_string()
    {
        const string options = "'initial-template': QName('', 'text')";
        (await FromQuery(options)).Should().Be("string:no markup");
        (await FromStylesheet(options)).Should().Be("string:no markup");
    }
}
