using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The shapes the streaming executor runs on the live reader, each shown to give the result
/// the tree gives. <c>StreamedBodyShape</c> holds the list of what may stream; a body that is
/// not on it runs on a buffered copy. This is the proof for the list: every instruction that
/// walks the input, in every position the list allows it, at every place a body can run.
/// </summary>
/// <remarks>
/// A shape belongs on the list only while its cases here pass. The cases at the end are bodies
/// that are NOT on the list (two walks, a walk and another read, a walk inside a loop): they
/// must give the tree result too, by way of the buffer.
/// </remarks>
public class StreamedShapeParityTests
{
    private const string Input =
        """<root a="A"><item id="1"><foo>f1</foo><bar>b1</bar><foo>f3</foo></item><item id="2"><foo>f2</foo><bar>b2</bar></item><tail>t</tail></root>""";

    private const string Head =
        """<xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all" version="3.0" expand-text="yes"><xsl:output omit-xml-declaration="yes"/><xsl:mode on-no-match="shallow-copy" streamable="yes"/>""";

    /// <summary>What walks the children of an element. {0} is the name of a child.</summary>
    private static readonly string[] ElementWalkers =
    [
        "<xsl:apply-templates/>",
        "<xsl:apply-templates select=\"{0}\"/>",
        "<xsl:apply-templates select=\"*\"/>",
        "<xsl:apply-templates select=\"node()\"/>",
        "<xsl:apply-templates select=\"item/foo\"/>",
        "<xsl:apply-templates select=\"{0}/bar\"/>",
        "<xsl:for-each select=\"{0}\"><e>{{.}}</e></xsl:for-each>",
        "<xsl:for-each select=\"*\">{{name()}}={{.}};</xsl:for-each>",
        "<xsl:for-each select=\"*\"><xsl:variable name=\"v\" select=\"string(.)\"/><xsl:if test=\"position() gt 1\">,</xsl:if>{{$v}}</xsl:for-each>",
        "<xsl:iterate select=\"*\"><xsl:param name=\"n\" select=\"0\"/><xsl:on-completion>[{{$n}}]</xsl:on-completion>{{name()}};<xsl:next-iteration><xsl:with-param name=\"n\" select=\"$n + string-length(.)\"/></xsl:next-iteration></xsl:iterate>",
        "<xsl:iterate select=\"{0}\">{{.}};</xsl:iterate>",
        "<xsl:for-each-group select=\"*\" group-adjacent=\"name()\"><g k=\"{{current-grouping-key()}}\" n=\"{{count(current-group())}}\"/></xsl:for-each-group>",
        "<xsl:for-each-group select=\"*\" group-starting-with=\"{0}\"><g>{{current-group() ! name()}}</g></xsl:for-each-group>",
        "<xsl:for-each-group select=\"*\" group-ending-with=\"{0}\"><g><xsl:copy-of select=\"current-group()\"/></g></xsl:for-each-group>",
    ];

    /// <summary>What may stand around a walk. {0} is the walk.</summary>
    private static readonly string[] Wrappers =
    [
        "{0}",
        "<w>{0}</w>",
        "<xsl:copy>{0}</xsl:copy>",
        "<xsl:element name=\"w\">{0}</xsl:element>",
        "<pre x=\"{{name()}}\"/><w>{0}</w><post/>",
        "<w><xsl:if test=\"true()\">{0}</xsl:if></w>",
        "<w><xsl:choose><xsl:when test=\"name() = 'none'\">no</xsl:when><xsl:otherwise>{0}</xsl:otherwise></xsl:choose></w>",
        "<w><xsl:try>{0}<xsl:catch>caught</xsl:catch></xsl:try></w>",
        "<w><xsl:where-populated><p>{0}</p></xsl:where-populated></w>",
        "<xsl:variable name=\"held\">{0}</xsl:variable><w><xsl:copy-of select=\"$held\"/></w>",
        "<w><xsl:document>{0}</xsl:document></w>",
    ];

    /// <summary>Where the rule for an element stands. {0} is its body; the child name follows.</summary>
    private static readonly (string Name, string Rules, string Child)[] ElementSites =
    [
        ("an element the pass reaches", "<xsl:template match=\"item\">{0}</xsl:template>", "foo"),
        ("the root element", "<xsl:template match=\"/*\">{0}</xsl:template>", "item"),
        ("a child handed over by xsl:apply-templates",
            "<xsl:template match=\"root\"><xsl:copy><xsl:apply-templates/></xsl:copy></xsl:template><xsl:template match=\"item\">{0}</xsl:template>", "foo"),
        ("a child handed over from the document rule",
            "<xsl:template match=\"/\"><d><xsl:apply-templates select=\"root/item\"/></d></xsl:template><xsl:template match=\"item\">{0}</xsl:template>", "foo"),
        ("a grandchild", "<xsl:template match=\"item\"><xsl:copy><xsl:apply-templates/></xsl:copy></xsl:template><xsl:template match=\"foo\">{0}</xsl:template>", "none"),
    ];

    /// <summary>What reads the input for a body whose context is the document node.</summary>
    private static readonly string[] DocumentReaders =
    [
        "<xsl:apply-templates/>",
        "<xsl:apply-templates select=\"root\"/>",
        "<xsl:apply-templates select=\"root/item\"/>",
        "<xsl:apply-templates select=\"root/item/foo\"/>",
        "<xsl:apply-templates select=\"node()\"/>",
        "<xsl:for-each select=\"root/item\"><e>{{foo}}</e></xsl:for-each>",
        "<xsl:for-each select=\"root/item/foo\">{{.}};</xsl:for-each>",
        "<xsl:for-each select=\"root/item\"><xsl:variable name=\"v\" select=\"string(bar)\"/>{{$v}};</xsl:for-each>",
        "<xsl:value-of select=\"count(root/item)\"/>",
        "{{count(//foo)}}",
        "<xsl:value-of select=\"sum(root/item/@id)\"/>",
        "<xsl:value-of select=\"string-join(root/item/foo, ',')\"/>",
        "{{count(root/item)}}/{{count(//bar)}}",
        "<xsl:copy-of select=\".\"/>",
        "<xsl:copy-of select=\"node()\"/>",
        "<xsl:sequence select=\"root/item/foo ! string()\"/>",
        "<xsl:value-of select=\"root/item ! upper-case(bar)\"/>",
    ];

    /// <summary>Bodies that are not on the list. They run on the buffer and must be right.</summary>
    private static readonly string[] ElementBodiesOffTheList =
    [
        "<xsl:apply-templates select=\"foo\"/><xsl:apply-templates select=\"bar\"/>",
        "<xsl:for-each select=\"foo\">{.}</xsl:for-each>|{bar}",
        "{count(*)}<xsl:apply-templates/>",
        "<xsl:variable name=\"n\" select=\"count(foo)\"/><xsl:for-each select=\"bar\">{.}:{$n}</xsl:for-each>",
        "<xsl:apply-templates><xsl:with-param name=\"p\" select=\"string(foo[1])\"/></xsl:apply-templates>",
        "<xsl:iterate select=\"*\"><xsl:param name=\"n\" select=\"count(bar)\"/>{$n}{name()};</xsl:iterate>",
        "<xsl:for-each select=\"*\"><xsl:sort select=\".\" order=\"descending\"/>{.};</xsl:for-each>",
        "<xsl:for-each-group select=\"*\" group-by=\"name()\">{current-grouping-key()}={count(current-group())};</xsl:for-each-group>",
        "<xsl:fork><xsl:sequence>{count(foo)}</xsl:sequence><xsl:sequence>,{count(bar)}</xsl:sequence></xsl:fork>",
        "<xsl:for-each select=\"foo[2]\">{.}</xsl:for-each>",
        "<xsl:for-each select=\"*\"><xsl:apply-templates select=\".\"/></xsl:for-each>",
        "<xsl:if test=\"foo = 'f1'\"><xsl:apply-templates/></xsl:if>",
        "<xsl:message select=\"string(bar)\"/><xsl:apply-templates/>",
    ];

    private static readonly string[] DocumentBodiesOffTheList =
    [
        "<out><xsl:apply-templates select=\"root/item\"/>{count(//foo)}</out>",
        "<out><xsl:for-each select=\"root/item\">{@id}</xsl:for-each><xsl:for-each select=\"root/tail\">{.}</xsl:for-each></out>",
        "<out>{root/item[2]/foo}</out>",
        "<out><xsl:value-of select=\"if (root/@a = 'A') then 'y' else 'n'\"/></out>",
        "<out><xsl:variable name=\"n\" select=\"count(root/item)\"/><xsl:for-each select=\"root/item\">{$n}</xsl:for-each></out>",
        "<out><xsl:iterate select=\"root/item\"><xsl:param name=\"n\" select=\"0\"/><xsl:on-completion>{$n}</xsl:on-completion><xsl:next-iteration><xsl:with-param name=\"n\" select=\"$n + 1\"/></xsl:next-iteration></xsl:iterate></out>",
        "<out><xsl:for-each-group select=\"root/item\" group-adjacent=\"@id\">{current-grouping-key()};</xsl:for-each-group></out>",
        "<out><xsl:for-each select=\"root/item\"><xsl:sort select=\"@id\" order=\"descending\"/>{@id}</xsl:for-each></out>",
        "<out>{exists(root/tail)}{string(root/tail)}</out>",
        "<out><xsl:message select=\"string(root/tail)\"/><xsl:apply-templates/></out>",
    ];

    public static TheoryData<string, string> ElementCases()
    {
        var cases = new TheoryData<string, string>();
        foreach (var (name, rules, child) in ElementSites)
        {
            foreach (var walker in ElementWalkers)
            {
                foreach (var wrapper in Wrappers)
                    cases.Add(name, Fill(rules, Fill(wrapper, Fill(walker, child))));
            }
            foreach (var body in ElementBodiesOffTheList)
                cases.Add(name, Fill(rules, body));
        }
        return cases;
    }

    public static TheoryData<string> DocumentCases()
    {
        var cases = new TheoryData<string>();
        foreach (var reader in DocumentReaders)
        {
            foreach (var wrapper in Wrappers)
                cases.Add(Fill(wrapper, Fill(reader, "")));
        }
        foreach (var body in DocumentBodiesOffTheList)
            cases.Add(body);
        return cases;
    }

    // The templates hold braces of their own (text value templates), so they are filled by
    // hand: {0} is the hole, {{ and }} are one brace each.
    private static string Fill(string template, string value)
        => template.Replace("{0}", value, StringComparison.Ordinal)
            .Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);

    private static async Task<string> RunAsync(string rules, bool tree)
    {
        var t = new XsltTransformer { DisableStreaming = tree };
        await t.LoadStylesheetAsync($"{Head}{rules}</xsl:stylesheet>");
        return await t.TransformAsync(Input);
    }

    [Theory]
    [MemberData(nameof(ElementCases))]
    public async Task A_rule_for_an_element_gives_the_tree_result(string site, string rules)
        => (await RunAsync(rules, tree: false)).Should().Be(await RunAsync(rules, tree: true), site);

    [Theory]
    [MemberData(nameof(DocumentCases))]
    public async Task A_rule_for_the_document_node_gives_the_tree_result(string body)
    {
        var rules = $"<xsl:template match=\"/\">{body}</xsl:template>";
        (await RunAsync(rules, tree: false)).Should().Be(await RunAsync(rules, tree: true));
    }

    [Theory]
    [MemberData(nameof(DocumentCases))]
    public async Task A_streamed_source_document_gives_the_tree_result(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"streamed-shape-parity-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(path, Input, TestContext.Current.CancellationToken);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(
                $"{Head}<xsl:template name=\"main\"><xsl:source-document streamable=\"yes\" href=\"{new Uri(path).AbsoluteUri}\">{body}</xsl:source-document></xsl:template></xsl:stylesheet>");
            t.SetInitialTemplate("main");
            (await t.TransformAsync((string?)null)).Should().Be(await RunAsync($"<xsl:template match=\"/\">{body}</xsl:template>", tree: true));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
