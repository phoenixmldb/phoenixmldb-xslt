using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// json-to-xml and characters XML cannot hold (F&amp;O 3.1 §17.5.3). With escape=false they become
/// U+FFFD; with escape=true the special characters are written as JSON escapes and the element
/// says so with escaped / escaped-key.
/// </summary>
public sealed class JsonToXmlSpecialCharacterTests
{
    private static async Task<string> RunAsync(string select)
    {
        var xsl = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
                  "<xsl:output method='xml' omit-xml-declaration='yes'/>" +
                  "<xsl:template name='xsl:initial-template'><out><xsl:copy-of select=\"" + select + "\"/></out></xsl:template></xsl:stylesheet>";
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task An_invalid_XML_character_becomes_the_replacement_character()
        => (await RunAsync("json-to-xml('[&quot;xx\\u0000xx&quot;]')//*:string/string()"))
            .Should().Be("<out>xx�xx</out>");

    [Fact]
    public async Task With_escape_a_special_character_is_written_as_its_JSON_escape()
        => (await RunAsync("json-to-xml('{&quot;k\\u000Cey&quot;:&quot;a\\u000Cb\\\\c&quot;}', map{'escape':true()})//*:string"))
            .Should().Contain(@"key=""k\fey""").And.Contain(@"escaped-key=""true""")
            .And.Contain(@"escaped=""true""").And.Contain(@">a\fb\\c<");

    [Fact]
    public async Task With_escape_an_unescaped_quote_is_not_marked_escaped()
        => (await RunAsync("json-to-xml('[&quot;a\\&quot;b&quot;]', map{'escape':true()})//*:string"))
            .Should().NotContain("escaped=");
}
