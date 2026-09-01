using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A global variable declared BEFORE the globals it selects from must still be evaluated after
/// them — including when the names are namespaced.
/// </summary>
/// <remarks>
/// <para>
/// Dependencies were collected by testing the variable REFERENCE (produced by the XPath parser)
/// for membership in the set of declared names (produced by the stylesheet parser). For an
/// unprefixed name both carry <c>NamespaceId.None</c> and the lookup hits. For a NAMESPACED name
/// the interned ids can differ, the lookup misses, and the dependency is never recorded.
/// </para>
/// <para>
/// Nothing errors. The topological sort simply does not order the pair, so the forward-declared
/// global is evaluated before the ones it reads and sees unbound values. It surfaced far from
/// the cause, as node kinds collapsing: <c>comment()</c> and <c>element()</c> came back as
/// documents, <c>attribute()</c> as an empty string, <c>namespace-node()</c> as text — and an
/// empty string then failed <c>except</c> with "an operand is not a node".
/// </para>
/// </remarks>
public class NamespacedGlobalDependencyOrderTests
{
    private const string Kind =
        "{if (. instance of comment()) then 'comment'"
        + " else if (. instance of element()) then 'element'"
        + " else if (. instance of attribute()) then 'attribute'"
        + " else if (. instance of namespace-node()) then 'namespace'"
        + " else if (. instance of document-node()) then 'DOCUMENT'"
        + " else if (. instance of text()) then 'TEXT'"
        + " else if (. instance of xs:string) then 'STRING' else 'other'}";

    private static async Task<string> RunAsync(string prefix)
    {
        // $q is "m:" for the namespaced form and "" for the unprefixed control.
        var q = prefix;
        var xmlns = prefix.Length > 0 ? "xmlns:m=\"urn:test:m\" " : "";
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" " + xmlns
            + "version=\"3.0\" exclude-result-prefixes=\"#all\">"
            // aggregates declared FIRST, two levels deep
            + $"<xsl:variable as=\"node()+\" name=\"{q}all\" select=\"${q}wrap, ${q}nonwrap\"/>"
            + $"<xsl:variable as=\"node()+\" name=\"{q}wrap\" select=\"${q}com, ${q}elem\"/>"
            + $"<xsl:variable as=\"node()+\" name=\"{q}nonwrap\" select=\"${q}attr, ${q}ns\"/>"
            + $"<xsl:variable as=\"comment()\" name=\"{q}com\"><xsl:comment>c</xsl:comment></xsl:variable>"
            + $"<xsl:variable as=\"element(e)\" name=\"{q}elem\"><e/></xsl:variable>"
            + $"<xsl:variable as=\"attribute(a)\" name=\"{q}attr\"><xsl:attribute name=\"a\">v</xsl:attribute></xsl:variable>"
            + $"<xsl:variable as=\"namespace-node()\" name=\"{q}ns\"><xsl:namespace name=\"p\">urn:p</xsl:namespace></xsl:variable>"
            + "<xsl:template name=\"main\"><out>"
            + $"<xsl:for-each select=\"${q}all\"><k v=\"{Kind}\"/></xsl:for-each>"
            + "</out></xsl:template></xsl:stylesheet>";

        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    private const string Expected =
        "<out><k v=\"comment\"/><k v=\"element\"/><k v=\"attribute\"/><k v=\"namespace\"/></out>";

    /// <summary>The case that broke: namespaced names, forward-declared, two levels deep.</summary>
    [Fact]
    public async Task NamespacedGlobals_AreEvaluatedInDependencyOrder()
    {
        (await RunAsync("m:")).Should().Be(Expected);
    }

    /// <summary>
    /// The unprefixed control. This half always worked, and asserting it is the point: four
    /// isolations passed during triage precisely because they used unprefixed names, which hid
    /// the bug. Keeping both spellings side by side stops that happening again.
    /// </summary>
    [Fact]
    public async Task UnprefixedGlobals_AreEvaluatedInDependencyOrder()
    {
        (await RunAsync("")).Should().Be(Expected);
    }
}
