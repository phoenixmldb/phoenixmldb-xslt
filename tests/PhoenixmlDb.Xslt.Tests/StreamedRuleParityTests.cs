using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A streamed template rule or xsl:source-document gives the answer the tree gives (xslt#343,
/// after xslt#340). The rules mix an instruction that walks the input with another read of it,
/// or match the root element or the document node. Three defects made some of them wrong with
/// no error: a streamed xsl:for-each ran its body for every child element and not only for the
/// children its select names; an aggregate in a text value template was evaluated against an
/// empty document node; a rule for the document node lost what it wrote around a streamed
/// xsl:for-each.
/// </summary>
public class StreamedRuleParityTests
{
    private const string Input =
        """<root a="A"><item id="1"><foo>f1</foo><bar>b1</bar></item><item id="2"><foo>f2</foo><bar>b2</bar></item><tail>t</tail></root>""";

    private const string Head =
        """<xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all" version="3.0" expand-text="yes"><xsl:output omit-xml-declaration="yes"/><xsl:mode on-no-match="shallow-copy" streamable="yes"/><xsl:template name="n">N{name()}</xsl:template>""";

    public static TheoryData<string, string> Rules => new()
    {
        { "item", "<xsl:copy>" + "<xsl:variable name=\"v\" select=\"string(foo)\"/><xsl:for-each select=\"bar\">{.}</xsl:for-each>{$v}" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:for-each select=\"*\">{.}</xsl:for-each>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:for-each select=\"*\"><xsl:variable name=\"v\" select=\"string(.)\"/>{$v}</xsl:for-each>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:for-each select=\"*\"><xsl:value-of select=\"if (position() = 1) then . else 'x'\"/></xsl:for-each>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:for-each select=\"*\"><xsl:try>{.}<xsl:catch>e</xsl:catch></xsl:try></xsl:for-each>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:for-each select=\"*\">{for $x in . return string($x)}</xsl:for-each>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:for-each select=\"*\"><e n=\"{name()}\">{.}</e></xsl:for-each>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:for-each select=\"*\"><xsl:copy-of select=\".\"/></xsl:for-each>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:apply-templates/><x/>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<x/><xsl:apply-templates/>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:apply-templates select=\"foo\"/>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:attribute name=\"k\" select=\"@id\"/><xsl:apply-templates/>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:variable name=\"i\" select=\"string(@id)\"/><xsl:apply-templates/>{$i}" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:iterate select=\"*\"><xsl:variable name=\"v\" select=\"string(.)\"/>{$v};</xsl:iterate>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:iterate select=\"*\"><xsl:param name=\"n\" select=\"0\"/><xsl:on-completion>{$n}</xsl:on-completion><xsl:next-iteration><xsl:with-param name=\"n\" select=\"$n + string-length(.)\"/></xsl:next-iteration></xsl:iterate>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:for-each-group select=\"*\" group-adjacent=\"name()\">{current-grouping-key()}:{count(current-group())};</xsl:for-each-group>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:for-each-group select=\"*\" group-adjacent=\"name()\"><xsl:variable name=\"g\" select=\"current-group() ! string()\"/>{$g}</xsl:for-each-group>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:fork><xsl:sequence>{foo}</xsl:sequence><xsl:sequence>{bar}</xsl:sequence></xsl:fork>" + "</xsl:copy>" },
        { "item", "<xsl:copy>" + "<xsl:call-template name=\"n\"/>" + "</xsl:copy>" },
        { "/*", "<xsl:copy><xsl:variable name=\"a\" select=\"string(@a)\"/><xsl:apply-templates/>{$a}</xsl:copy>" },
        { "/*", "<xsl:copy><xsl:for-each select=\"item\"><xsl:variable name=\"v\" select=\"string(foo)\"/><i>{$v}</i></xsl:for-each></xsl:copy>" },
        { "/*", "<xsl:copy><xsl:for-each select=\"item\"><i><xsl:value-of select=\"if (@id = '1') then foo else bar\"/></i></xsl:for-each></xsl:copy>" },
        { "/*", "<xsl:copy><xsl:for-each select=\"item\"><i a=\"{foo}\"/></xsl:for-each></xsl:copy>" },
        { "/*", "<xsl:copy><xsl:for-each select=\"item\"><i>{foo}</i><j>{@id}</j></xsl:for-each></xsl:copy>" },
        { "/*", "<xsl:copy><xsl:for-each select=\"item/foo\">{.}</xsl:for-each></xsl:copy>" },
        { "/*", "<out n=\"{count(item)}\"/>" },
        { "/*", "<out><xsl:variable name=\"n\" select=\"count(item)\"/>{$n}</out>" },
        { "/*", "<out>{string-join(item/foo, ',')}</out>" },
        { "/*", "<out><xsl:value-of select=\"if (@a = 'A') then tail else item\"/></out>" },
        { "/", "<out><xsl:apply-templates select=\"root/item\"/></out>" },
        { "/", "<out>{count(root/item)}</out>" },
        { "/", "<out><xsl:for-each select=\"root/item\"><xsl:variable name=\"v\" select=\"string(bar)\"/>{$v}</xsl:for-each></out>" },
        { "/", "<out><xsl:value-of select=\"if (true()) then root/tail else ()\"/></out>" },
        { "/", "<out><xsl:copy-of select=\"root/item[1]\"/></out>" },
        { "/", "<out><xsl:for-each select=\"outermost(//bar)\"><xsl:try>{.}<xsl:catch/></xsl:try></xsl:for-each></out>" },
        { "item/foo", "<F>{.}</F>" },
        { "item/foo", "<xsl:variable name=\"v\" select=\"string(.)\"/><F>{$v}</F>" },
        { "item/foo/text()", "<xsl:variable name=\"v\" select=\"upper-case(.)\"/>{$v}" },
        { "foo", "<xsl:copy><xsl:value-of select=\"if (. = 'f1') then 'one' else 'other'\"/></xsl:copy>" },
        { "/", "<out><xsl:value-of select=\"count(root/item)\"/></out>" },
        { "/", "<out>{count(//foo)}</out>" },
        { "/", "<out><xsl:value-of select=\"count(//foo)\"/></out>" },
        { "/", "<out><xsl:variable name=\"n\" select=\"count(root/item)\"/>{$n}</out>" },
        { "/", "<out>{count(*/item)}</out>" },
        { "/", "<out>{count(/root/item)}</out>" },
        { "/", "<out>{sum(root/item/@id)}</out>" },
        { "/", "<out>{string-join(root/item/foo, ',')}</out>" },
        { "/", "<out a=\"{count(root/item)}\"/>" },
        { "/", "<out>{count(root/item) + 1}</out>" },
        { "/", "<out>{root/item/foo}</out>" },
        { "/", "<out><xsl:value-of select=\"root/tail\"/></out>" },
        { "/", "<out>{exists(root/tail)}</out>" },
        { "/", "<out><xsl:for-each select=\"root/item\">{bar}</xsl:for-each></out>" },
        { "/", "<out><xsl:for-each select=\"root/item\"><i>{bar}</i></xsl:for-each><z/></out>" },
        { "/", "<a><b><xsl:for-each select=\"root/item\"><xsl:copy-of select=\"foo\"/></xsl:for-each></b></a>" },
        { "/", "<xsl:for-each select=\"root/item\">{bar}</xsl:for-each>" },
        { "/", "<out><xsl:iterate select=\"root/item\">{bar}</xsl:iterate></out>" },
        { "/", "<out><xsl:for-each-group select=\"root/item\" group-adjacent=\"@id\">{current-grouping-key()}</xsl:for-each-group></out>" },
        { "/", "<out><xsl:copy-of select=\"root/item/foo\"/></out>" },
        { "/", "<out><xsl:sequence select=\"root/item/foo/string()\"/></out>" },
        { "/", "<out><xsl:if test=\"root/item\">y</xsl:if></out>" },
        { "/", "<out><xsl:choose><xsl:when test=\"root/@a = 'A'\">y</xsl:when><xsl:otherwise>n</xsl:otherwise></xsl:choose></out>" },
    };

    public static TheoryData<string> DocumentBodies => new()
    {
        "<out>{count(root/item)}</out>",
        "<out><xsl:value-of select=\"count(root/item)\"/></out>",
        "<out>{count(//foo)}</out>",
        "<out><xsl:value-of select=\"count(//foo)\"/></out>",
        "<out><xsl:variable name=\"n\" select=\"count(root/item)\"/>{$n}</out>",
        "<out>{count(*/item)}</out>",
        "<out>{count(/root/item)}</out>",
        "<out>{sum(root/item/@id)}</out>",
        "<out>{string-join(root/item/foo, ',')}</out>",
        "<out a=\"{count(root/item)}\"/>",
        "<out>{count(root/item) + 1}</out>",
        "<out>{root/item/foo}</out>",
        "<out><xsl:value-of select=\"root/tail\"/></out>",
        "<out>{exists(root/tail)}</out>",
        "<out><xsl:for-each select=\"root/item\"><xsl:variable name=\"v\" select=\"string(bar)\"/>{$v}</xsl:for-each></out>",
        "<out><xsl:for-each select=\"root/item\">{bar}</xsl:for-each></out>",
        "<out><xsl:for-each select=\"root/item\"><i>{bar}</i></xsl:for-each><z/></out>",
        "<a><b><xsl:for-each select=\"root/item\"><xsl:copy-of select=\"foo\"/></xsl:for-each></b></a>",
        "<xsl:for-each select=\"root/item\">{bar}</xsl:for-each>",
        "<out><xsl:iterate select=\"root/item\">{bar}</xsl:iterate></out>",
        "<out><xsl:for-each-group select=\"root/item\" group-adjacent=\"@id\">{current-grouping-key()}</xsl:for-each-group></out>",
        "<out><xsl:copy-of select=\"root/item/foo\"/></out>",
        "<out><xsl:sequence select=\"root/item/foo/string()\"/></out>",
        "<out><xsl:if test=\"root/item\">y</xsl:if></out>",
        "<out><xsl:choose><xsl:when test=\"root/@a = 'A'\">y</xsl:when><xsl:otherwise>n</xsl:otherwise></xsl:choose></out>",
    };

    private static async Task<string> RunAsync(string match, string body, bool tree)
    {
        var t = new XsltTransformer { DisableStreaming = tree };
        await t.LoadStylesheetAsync($"{Head}<xsl:template match='{match}'>{body}</xsl:template></xsl:stylesheet>");
        return await t.TransformAsync(Input);
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public async Task The_streamed_result_of_a_rule_is_the_tree_result(string match, string body)
        => (await RunAsync(match, body, tree: false)).Should().Be(await RunAsync(match, body, tree: true));

    [Theory]
    [MemberData(nameof(DocumentBodies))]
    public async Task The_result_of_a_streamed_source_document_is_the_tree_result(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"streamed-rule-parity-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(path, Input, TestContext.Current.CancellationToken);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(
                $"{Head}<xsl:template name='main'><xsl:source-document streamable='yes' href='{new Uri(path).AbsoluteUri}'>{body}</xsl:source-document></xsl:template></xsl:stylesheet>");
            t.SetInitialTemplate("main");
            (await t.TransformAsync((string?)null)).Should().Be(await RunAsync("/", body, tree: true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("foo", "f1")]
    [InlineData("bar", "b1")]
    [InlineData("nothing", "")]
    public async Task A_streamed_for_each_runs_its_body_only_for_the_children_its_select_names(string name, string expected)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(
            $"{Head}<xsl:template match='item[@id = \"1\"]'><r><xsl:for-each select='{name}'>{{.}}</xsl:for-each></r></xsl:template><xsl:template match='item'/></xsl:stylesheet>");
        (await t.TransformAsync(Input)).Should().Contain(expected.Length == 0 ? "<r/>" : $"<r>{expected}</r>");
    }
}
