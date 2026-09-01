using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>fn:transform</c>'s parameter-bearing options — <c>stylesheet-params</c>,
/// <c>template-params</c> and <c>tunnel-params</c> — under <c>delivery-format: 'raw'</c>, which
/// is the path <c>XsltTransformFunction</c> takes when fn:transform is called from inside a
/// stylesheet.
/// </summary>
/// <remarks>
/// <para>
/// Two independent defects meet here, and both reduce to a parameter silently keeping its
/// default rather than raising anything.
/// </para>
/// <para>
/// The first is delivery: <c>TransformAsync</c> built a with-param list and passed it to
/// ApplyTemplatesAsync, while the raw path passed an empty list at every one of its initial-mode
/// call sites — so a scenario driving an initial MODE never received template-params at all.
/// </para>
/// <para>
/// The second is matching: every key in these option maps arrives from <c>fn:QName()</c> at
/// runtime, carrying a hash-based <c>NamespaceId</c> that never equals the id the stylesheet
/// parser interned for the same URI, and <see cref="PhoenixmlDb.Core.QName"/> equality compares
/// that id. A NAMESPACED key therefore misses every lookup that a no-namespace key hits. Each
/// test below pairs the two in ONE option map so the namespace is the only difference between
/// them. See <c>QNameNamespaces</c>, which exists because this trap had already cost three
/// separate bugs.
/// </para>
/// </remarks>
public class TransformStylesheetParamsTests
{
    /// <summary>
    /// The stylesheet under test: one no-namespace and one namespaced global parameter, a named
    /// template taking one of each as template parameters, and a mode whose matching template
    /// takes a template parameter. Passed as <c>stylesheet-text</c> so nothing touches disk.
    /// </summary>
    private static string TargetStylesheet() =>
        "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\" "
        + "xmlns:t=\"urn:test:t\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" "
        + "exclude-result-prefixes=\"#all\">"
        + "<xsl:param name=\"plain\" as=\"xs:string\" select=\"'PLAIN-DEFAULT'\"/>"
        + "<xsl:param name=\"t:ns\" as=\"xs:string\" select=\"'NS-DEFAULT'\"/>"
        + "<xsl:variable name=\"notaparam\" as=\"xs:string\" select=\"'VARIABLE-VALUE'\"/>"
        + "<xsl:mode name=\"pm\" on-no-match=\"fail\"/>"
        + "<xsl:template match=\"/ | node()\" mode=\"pm\" as=\"item()*\">"
        + "<xsl:param name=\"tplain\" as=\"item()*\" select=\"'TPLAIN-DEFAULT'\"/>"
        + "<xsl:sequence select=\"'plainmode-tplain=' || string-join($tplain ! string(.), ',')\"/>"
        + "</xsl:template>"
        + "<xsl:mode name=\"t:pm\" on-no-match=\"fail\"/>"
        + "<xsl:template match=\"/ | node()\" mode=\"t:pm\" as=\"item()*\">"
        + "<xsl:param name=\"tplain\" as=\"item()*\" select=\"'TPLAIN-DEFAULT'\"/>"
        + "<xsl:param name=\"t:tns\" as=\"item()*\" select=\"'TNS-DEFAULT'\"/>"
        + "<xsl:sequence select=\"'mode-tplain=' || string-join($tplain ! string(.), ',')"
        + " || ' mode-tns=' || string-join($t:tns ! string(.), ',')\"/>"
        + "</xsl:template>"
        + "<xsl:template name=\"t:echo\" as=\"item()*\">"
        + "<xsl:param name=\"tplain\" as=\"item()*\" select=\"'TPLAIN-DEFAULT'\"/>"
        + "<xsl:param name=\"t:tns\" as=\"item()*\" select=\"'TNS-DEFAULT'\"/>"
        + "<xsl:sequence select=\"'plain=' || $plain || ' ns=' || $t:ns"
        + " || ' var=' || $notaparam"
        + " || ' tplain=' || string-join($tplain ! string(.), ',')"
        + " || ' tns=' || string-join($t:tns ! string(.), ',')\"/>"
        + "</xsl:template></xsl:stylesheet>";

    private static async Task<string> RunDriverAsync(string selectionEntries, string optionEntries)
    {
        // Order matters, and getting it backwards is silent: the target is embedded as an XPath
        // string literal INSIDE an XML attribute, so it needs the XPath escape (double every
        // apostrophe) applied FIRST and the XML escape applied over the top. Escaping for XML
        // first turns every apostrophe into &apos;, leaving nothing for the XPath escape to
        // double — and the XML parser then hands a bare apostrophe back to the XPath parser,
        // which ends the string literal early.
        var escaped = System.Security.SecurityElement.Escape(
            TargetStylesheet().Replace("'", "''", StringComparison.Ordinal))!;

        var driver =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\" "
            + "xmlns:t=\"urn:test:t\" exclude-result-prefixes=\"#all\">"
            + "<xsl:template name=\"main\">"
            + "<xsl:variable name=\"src\"><doc/></xsl:variable>"
            + "<xsl:variable name=\"r\" select=\"transform(map { "
            + "'stylesheet-text': '" + escaped + "', "
            + "'delivery-format': 'raw', "
            + selectionEntries + ", "
            + optionEntries
            + " })\"/>"
            + "<out><xsl:value-of select=\"$r?output\"/></out>"
            + "</xsl:template></xsl:stylesheet>";

        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(driver);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    /// <summary>Invokes the target by naming an initial template.</summary>
    private static Task<string> RunTemplateAsync(string optionEntries)
        => RunDriverAsync("'initial-template': QName('urn:test:t', 't:echo')", optionEntries);

    /// <summary>Invokes the target by naming an initial mode over a source node.</summary>
    private static Task<string> RunModeAsync(string optionEntries)
        => RunDriverAsync(
            "'source-node': $src, 'initial-mode': QName('urn:test:t', 't:pm')", optionEntries);

    /// <summary>
    /// Invokes the target by naming an initial mode that has NO namespace, so a QName built at
    /// runtime and one interned by the parser agree on their NamespaceId. This isolates
    /// parameter DELIVERY from initial-mode RESOLUTION — a namespaced initial mode fails to
    /// resolve at all and falls back to the built-in templates, which produce empty output and
    /// hide whatever the parameter path would have done.
    /// </summary>
    private static Task<string> RunPlainModeAsync(string optionEntries)
        => RunDriverAsync("'source-node': $src, 'initial-mode': QName('', 'pm')", optionEntries);

    /// <summary>
    /// A no-namespace global parameter. This half already worked — it is here so a regression in
    /// the shared delivery path stays distinguishable from a regression in name matching.
    /// </summary>
    [Fact]
    public async Task StylesheetParams_DeliversNoNamespaceGlobal()
    {
        var result = await RunTemplateAsync(
            "'stylesheet-params': map { QName('', 'plain'): 'PASSED-PLAIN' }");
        result.Should().Contain("plain=PASSED-PLAIN");
    }

    /// <summary>
    /// A NAMESPACED global parameter, supplied in the same map as the no-namespace one, so the
    /// namespace is the only difference. Before the fix this reported <c>ns=NS-DEFAULT</c>.
    /// </summary>
    [Fact]
    public async Task StylesheetParams_DeliversNamespacedGlobal()
    {
        var result = await RunTemplateAsync(
            "'stylesheet-params': map { QName('', 'plain'): 'PASSED-PLAIN', "
            + "QName('urn:test:t', 't:ns'): 'PASSED-NS' }");
        result.Should().Contain("plain=PASSED-PLAIN");
        result.Should().Contain("ns=PASSED-NS");
    }

    /// <summary>
    /// <c>template-params</c> against a named initial template, both namespaced and not.
    /// </summary>
    [Fact]
    public async Task TemplateParams_DeliversToInitialTemplate()
    {
        var result = await RunTemplateAsync(
            "'template-params': map { QName('', 'tplain'): 'PASSED-TPLAIN', "
            + "QName('urn:test:t', 't:tns'): 'PASSED-TNS' }");
        result.Should().Contain("tplain=PASSED-TPLAIN");
        result.Should().Contain("tns=PASSED-TNS");
    }

    /// <summary>
    /// <c>template-params</c> delivered to an initial MODE rather than an initial template.
    /// This is the shape XSpec compiles a scenario-level <c>x:param</c> into when the scenario
    /// uses <c>x:context/@mode</c>, and it is what terminated the <c>external_context-param</c>
    /// suite: the raw path called ApplyTemplatesAsync with an empty with-param list, so the
    /// matched template saw its default and the assertion compared against the empty sequence.
    /// </summary>
    [Fact]
    public async Task TemplateParams_DeliveredToInitialMode()
    {
        var result = await RunModeAsync(
            "'template-params': map { QName('', 'tplain'): 'PASSED-TPLAIN', "
            + "QName('urn:test:t', 't:tns'): 'PASSED-TNS' }");
        result.Should().Contain("mode-tplain=PASSED-TPLAIN");
        result.Should().Contain("mode-tns=PASSED-TNS");
    }

    /// <summary>
    /// <c>template-params</c> delivered to a NO-NAMESPACE initial mode. Both the mode name and
    /// the parameter name dodge the QName identity trap, so this test sees only the delivery
    /// path: the raw-delivery transform passed an empty with-param list to ApplyTemplatesAsync
    /// while its TransformAsync twin passed the built list.
    /// </summary>
    [Fact]
    public async Task TemplateParams_DeliveredToNoNamespaceInitialMode()
    {
        var result = await RunPlainModeAsync(
            "'template-params': map { QName('', 'tplain'): 'PASSED-TPLAIN' }");
        result.Should().Contain("plainmode-tplain=PASSED-TPLAIN");
    }

    /// <summary>
    /// A supplied value whose name matches a global <c>xsl:variable</c> rather than an
    /// <c>xsl:param</c> must be IGNORED — the variable keeps its computed value (XSLT 3.0 §9.5).
    /// </summary>
    /// <remarks>
    /// The engine bound every supplied name straight into global scope without asking whether
    /// the declaration was a parameter, so a caller could silently replace a variable's value,
    /// or blank it. Harmless-looking while fn:transform's stylesheet-params were not delivered
    /// at all; the moment they were, it broke XSpec's global-override suite, whose entire
    /// purpose is asserting exactly this rule.
    ///
    /// The param and the variable are supplied in ONE map so the only difference between them
    /// is which kind of declaration they land on.
    /// </remarks>
    [Fact]
    public async Task StylesheetParams_DoNotOverrideAGlobalVariable()
    {
        var result = await RunTemplateAsync(
            "'stylesheet-params': map { QName('', 'plain'): 'PASSED-PLAIN', "
            + "QName('', 'notaparam'): 'SHOULD-BE-IGNORED' }");
        result.Should().Contain("plain=PASSED-PLAIN");
        result.Should().Contain("var=VARIABLE-VALUE");
        result.Should().NotContain("SHOULD-BE-IGNORED");
    }
}
