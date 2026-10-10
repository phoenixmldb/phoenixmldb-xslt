using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A template rule of a streamable mode gives the answer the tree gives, whatever its body
/// (xslt#340). The bodies are ordinary ones. On the live reader eighteen of them were wrong with
/// no error: a body that reads the children of the matched element and has no instruction to
/// walk them (a variable, a conditional expression, an attribute value, xsl:try) saw no
/// children, or was run late and lost the end tags of the elements around it.
/// </summary>
public class StreamedBodyParityTests
{
    private const string Input =
        """<root><item id="1"><foo>f1</foo><bar>b1</bar></item><item id="2"><foo>f2</foo><bar>b2</bar></item></root>""";

    public static TheoryData<string> Bodies => new()
    {
            "<xsl:value-of select=\"if (@id = '1') then foo else bar\"/>",
            "<xsl:value-of select=\"if (@id = '1') then foo else 'x'\"/>",
            "<xsl:value-of select=\"if (@id = '1') then 'x' else bar\"/>",
            "<xsl:value-of select=\"if (@id = '1') then string(foo) else string(bar)\"/>",
            "<xsl:copy-of select=\"if (@id = '1') then foo else bar\"/>",
            "<xsl:sequence select=\"if (@id = '1') then string(foo) else 'x'\"/>",
            "<xsl:variable name=\"m\" select=\"map { 'a': string(foo) }\"/>{$m?a}",
            "<xsl:variable name=\"v\" select=\"string(foo)\"/>{$v}",
            "<xsl:variable name=\"v\" select=\"string(foo)\"/><a>{$v}</a>",
            "<xsl:variable name=\"v\" select=\"copy-of(foo)\"/>{$v}",
            "<xsl:variable name=\"v\" as=\"xs:string\" select=\"foo\"/>{$v}",
            "<xsl:variable name=\"v\"><xsl:value-of select=\"foo\"/></xsl:variable>{$v}",
            "<xsl:variable name=\"m\" select=\"map { 'a': 1 }\"/>{$m?a}{foo}",
            "<xsl:variable name=\"a\" select=\"[string(foo)]\"/>{$a?1}",
            "{(foo, 'x')[1]}",
            "{foo ! upper-case(.)}",
            "{for $x in foo return string($x)}",
            "{let $x := string(foo) return $x || $x}",
            "{string-join(* ! name(), ',')}",
            "{sum(* ! string-length(.))}",
            "{count(*)}",
            "{exists(foo)}",
            "{foo/text()}",
            "{*[1]}",
            "{*[2]}",
            "{foo[. = 'f1']}",
            "{.//text()}",
            "{string(.)}",
            "{data(foo)}",
            "{head(*)}",
            "{tail(*)}",
            "{subsequence(*, 2)}",
            "<xsl:if test=\"@id = '1'\">{foo}</xsl:if>",
            "<xsl:choose><xsl:when test=\"@id = '1'\">{foo}</xsl:when><xsl:otherwise>{bar}</xsl:otherwise></xsl:choose>",
            "<xsl:for-each select=\"*\"><xsl:if test=\"position() = 2\">{.}</xsl:if></xsl:for-each>",
            "<xsl:for-each select=\"*\"><xsl:variable name=\"n\" select=\"name()\"/>{$n}={.};</xsl:for-each>",
            "<xsl:iterate select=\"*\"><xsl:param name=\"acc\" select=\"''\"/><xsl:on-completion>{$acc}</xsl:on-completion><xsl:next-iteration><xsl:with-param name=\"acc\" select=\"$acc || string(.)\"/></xsl:next-iteration></xsl:iterate>",
            "<xsl:try>{foo}<xsl:catch>err</xsl:catch></xsl:try>",
            "<xsl:where-populated><w>{foo}</w></xsl:where-populated>",
            "<xsl:element name=\"{name()}x\">{foo}</xsl:element>",
            "<xsl:attribute name=\"a\" select=\"foo\"/>",
            "<xsl:copy-of select=\"foo\"/>",
            "<xsl:copy-of select=\"*\"/>",
            "<xsl:sequence select=\"copy-of(bar)\"/>",
            "<xsl:value-of select=\"snapshot(foo)/..//bar\"/>",
            "<xsl:apply-templates select=\"bar\"/>",
            "<xsl:apply-templates select=\"@*\"/><xsl:apply-templates select=\"bar\"/>",
            "<xsl:message select=\"string(foo)\"/>ok",
    };

    private static async Task<string> RunAsync(string body, bool tree)
    {
        var t = new XsltTransformer { DisableStreaming = tree };
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all" version="3.0" expand-text="yes">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
              <xsl:template match="item"><xsl:copy>{body}</xsl:copy></xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync(Input);
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task The_streamed_result_is_the_tree_result(string body)
        => (await RunAsync(body, tree: false)).Should().Be(await RunAsync(body, tree: true));
}
