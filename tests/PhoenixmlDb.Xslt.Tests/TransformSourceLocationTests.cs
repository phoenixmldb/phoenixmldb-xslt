using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// fn:transform's <c>source-location</c> option, and the parity between the two hosts that can
/// call fn:transform (Martin Honnen, xslt#173).
/// </summary>
/// <remarks>
/// <para>
/// There are two fn:transform implementations — <c>Engine/XsltTransformFunction</c> for callers
/// inside a stylesheet and <c>XsltTransformProvider</c> for callers inside a query — and #173 was
/// one defect in each, pointing opposite ways:
/// </para>
/// <list type="bullet">
///   <item><description>the XSLT side never read <c>source-location</c>, so the principal input
///   fell through to a literal <c>&lt;empty/&gt;</c> and the inner transform ran against nothing;</description></item>
///   <item><description>the XQuery side dropped the RESULT under <c>delivery-format='raw'</c>
///   whenever the inner template constructed nodes, returning an empty <c>?output</c> and exit
///   code 0 — silently.</description></item>
/// </list>
/// <para>
/// Each host had the fix the other lacked. What was missing was any test comparing the two, so
/// the <see cref="BothHosts_AgreeOn_TheSameTransform"/> cases matter more than the two
/// single-host regression tests: they are what fails when these implementations drift again.
/// </para>
/// </remarks>
public sealed class TransformSourceLocationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phoenixml-srcloc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Writes the shared fixture pair and returns (inputUri, innerStylesheetUri).</summary>
    private async Task<(string Input, string Inner)> WriteFixturesAsync()
    {
        var input = Path.Combine(_dir, "input.xml");
        await File.WriteAllTextAsync(input,
            "<doc><h1>First</h1><p>a</p><h1>Second</h1><p>b</p></doc>").ConfigureAwait(true);

        // Constructs an element, deliberately: a node-constructing body writes to the output
        // buffer rather than the sequence collector, which is the case the XQuery-side raw
        // delivery lost.
        var inner = Path.Combine(_dir, "inner.xsl");
        await File.WriteAllTextAsync(inner, """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><out>
                <xsl:for-each-group select="doc/*" group-starting-with="h1"
                  ><g n="{count(current-group())}"/></xsl:for-each-group></out></xsl:template>
            </xsl:stylesheet>
            """).ConfigureAwait(true);

        return (new Uri(input).AbsoluteUri, new Uri(inner).AbsoluteUri);
    }

    private const string Expected = "<out><g n=\"2\"/><g n=\"2\"/></out>";

    /// <summary>Runs the transform from inside a stylesheet (the XsltTransformFunction path).</summary>
    private static async Task<string> ViaXsltAsync(string optionEntries)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes" indent="no"/>
              <xsl:template match="/" name="xsl:initial-template"
                ><xsl:copy-of select="transform(map { {{optionEntries}} })?output"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null).ConfigureAwait(true)).Trim();
    }

    /// <summary>Runs the same transform from inside a query (the XsltTransformProvider path).</summary>
    private static async Task<string> ViaXQueryAsync(string optionEntries)
        => (await new PhoenixmlDb.XQuery.XQueryFacade()
            .EvaluateAsync($"serialize(transform(map {{ {optionEntries} }})?output)")
            .ConfigureAwait(true)).Trim();

    // ── Fault 1: source-location was not read on the XSLT side ────────────────────────────

    /// <summary>
    /// The reported case. <c>source-location</c> was never read by the stylesheet-side
    /// implementation, so <c>hasSource</c> stayed false and both delivery branches handed the
    /// inner engine a literal <c>&lt;empty/&gt;</c> — the <c>&lt;empty/&gt;</c> in the report.
    /// </summary>
    [Theory]
    [InlineData("raw")]
    [InlineData("document")]
    public async Task Xslt_SourceLocation_IsUsedAsThePrincipalInput(string deliveryFormat)
    {
        var (input, inner) = await WriteFixturesAsync().ConfigureAwait(true);
        (await ViaXsltAsync(
                $"'source-location':'{input}', 'stylesheet-location':'{inner}', 'delivery-format':'{deliveryFormat}'")
            .ConfigureAwait(true))
            .Should().Be(Expected,
                "source-location must load the principal input; an unread option left the inner "
                + "transform running against a literal <empty/>");
    }

    /// <summary>
    /// source-location and source-node must reach the same result — the comparison that localised
    /// the fault in the first place.
    /// </summary>
    [Fact]
    public async Task Xslt_SourceLocation_MatchesSourceNode()
    {
        var (input, inner) = await WriteFixturesAsync().ConfigureAwait(true);
        var viaLocation = await ViaXsltAsync(
            $"'source-location':'{input}', 'stylesheet-location':'{inner}', 'delivery-format':'raw'").ConfigureAwait(true);
        var viaNode = await ViaXsltAsync(
            $"'source-node':doc('{input}'), 'stylesheet-location':'{inner}', 'delivery-format':'raw'").ConfigureAwait(true);
        viaLocation.Should().Be(viaNode);
    }

    /// <summary>
    /// Saxon's precedence, which the provider already documented and the stylesheet side now
    /// shares: source-node wins when both are supplied. Asserted with a source-location that
    /// does not exist, so a regression that preferred it fails loudly rather than subtly.
    /// </summary>
    [Fact]
    public async Task Xslt_SourceNode_BeatsSourceLocation()
    {
        var (input, inner) = await WriteFixturesAsync().ConfigureAwait(true);
        var missing = new Uri(Path.Combine(_dir, "no-such-file.xml")).AbsoluteUri;
        (await ViaXsltAsync(
                $"'source-node':doc('{input}'), 'source-location':'{missing}', "
                + $"'stylesheet-location':'{inner}', 'delivery-format':'raw'").ConfigureAwait(true))
            .Should().Be(Expected);
    }

    /// <summary>
    /// An unreadable source-location must say so, not quietly transform nothing.
    /// </summary>
    /// <remarks>
    /// The assertion names the missing FILE deliberately. Asserting merely that "something throws"
    /// passed before the fix too — with the option unread there was no source document at all, so
    /// the inner transform raised XTDE0040 and a laxer test would have gone green against the very
    /// defect it was written for. The filename can only appear once the fetch is actually
    /// attempted.
    /// </remarks>
    [Fact]
    public async Task Xslt_SourceLocation_ThatCannotBeRead_Reports()
    {
        var (_, inner) = await WriteFixturesAsync().ConfigureAwait(true);
        var missing = new Uri(Path.Combine(_dir, "no-such-file.xml")).AbsoluteUri;
        var act = async () => await ViaXsltAsync(
            $"'source-location':'{missing}', 'stylesheet-location':'{inner}'").ConfigureAwait(true);
        (await act.Should().ThrowAsync<Exception>().ConfigureAwait(true))
            .Which.Message.Should().Contain("no-such-file",
                "the failure must name the source-location that could not be read; silently "
                + "substituting an empty document is what made #173 hard to see");
    }

    // ── Fault 2: raw delivery dropped the result on the XQuery side ────────────────────────

    /// <summary>
    /// <c>delivery-format='raw'</c> from a query returned nothing at all — the facade boxed only
    /// typed values and discarded the serialized buffer, so a node-CONSTRUCTING template had
    /// nothing to hand back. Exit code 0 with no output, for every such stylesheet.
    /// </summary>
    [Theory]
    [InlineData("raw")]
    [InlineData("document")]
    public async Task XQuery_DeliversTheResult_ForANodeConstructingTemplate(string deliveryFormat)
    {
        var (input, inner) = await WriteFixturesAsync().ConfigureAwait(true);
        (await ViaXQueryAsync(
                $"'source-location':'{input}', 'stylesheet-location':'{inner}', 'delivery-format':'{deliveryFormat}'")
            .ConfigureAwait(true))
            .Should().Contain("<g n=\"2\"/>",
                "raw delivery returned an empty ?output whenever the inner template constructed "
                + "nodes rather than returning a typed value");
    }

    // ── The check whose absence let both faults through ───────────────────────────────────

    /// <summary>
    /// The two implementations must agree. Neither fault in #173 could have survived a test that
    /// ran the same options through both hosts, and this is the case that fails first the next
    /// time they drift.
    /// </summary>
    [Theory]
    [InlineData("raw")]
    [InlineData("document")]
    public async Task BothHosts_AgreeOn_TheSameTransform(string deliveryFormat)
    {
        var (input, inner) = await WriteFixturesAsync().ConfigureAwait(true);
        var opts = $"'source-location':'{input}', 'stylesheet-location':'{inner}', "
                 + $"'delivery-format':'{deliveryFormat}'";

        var fromXslt = await ViaXsltAsync(opts).ConfigureAwait(true);
        var fromXQuery = await ViaXQueryAsync(opts).ConfigureAwait(true);

        // Serialization differs in incidental ways between the hosts (the query route goes through
        // fn:serialize); compare the content that matters.
        static string Normalize(string s) => s
            .Replace("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", "", StringComparison.Ordinal)
            .Replace("<?xml version=\"1.0\" encoding=\"utf-8\"?>", "", StringComparison.Ordinal)
            .Replace(" />", "/>", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .Trim();

        Normalize(fromXQuery).Should().Be(Normalize(fromXslt),
            "fn:transform must mean the same thing from a stylesheet and from a query; #173 was "
            + "one defect in each direction because nothing compared them");
    }
}
