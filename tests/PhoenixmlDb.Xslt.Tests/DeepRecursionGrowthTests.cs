using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The time of a recursion does not grow with the square of its depth (#639). Each level did work
/// in proportion to the number of levels above it (the tree builder's ancestor walks, a copied
/// source element's base URI and namespaces, the tunnel-parameter search), so 10,000 elements nested
/// 10,000 deep took many times as long as the same 10,000 elements two levels deep; now less than
/// twice as long.
/// </summary>
/// <remarks>
/// <para>
/// Two inputs of the same size that differ only in depth, measured in turn, the best of five of
/// each, and compared: deep against wide. A comparison of one depth with twice that depth also
/// separates linear from quadratic growth (2 against 4), but the time a runner measures for a
/// larger input depends on the runner, and such a test of the same kind (phoenixmldb-xquery#370)
/// failed on Windows CI with correct code. With this change the deep input takes less than twice as
/// long as the wide one, before it many times as long; the bound, 5, is far from both.
/// </para>
/// <para>
/// The time counted is the time without garbage-collection pauses. A collection scans the whole
/// stack, and a deep recursion makes the stack deep, so the deep input's pauses are longer whatever
/// the engine does per level; when a collection falls in a run is noise as well.
/// </para>
/// <para>The class runs alone, so other tests do not share the processor.</para>
/// </remarks>
[Collection(nameof(DeepRecursionGrowthSerial))]
public sealed class DeepRecursionGrowthTests(ITestOutputHelper output)
{
    private const int Elements = 10_000;

    // Each element is copied (the base URI and the in-scope namespaces of a source element), its
    // template declares a tunnel parameter that is passed and one that is not, and templates are
    // applied to its children: in the deep input that is a recursion 10,000 levels deep.
    private const string Stylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:template match="/">
            <xsl:variable name="r"><r><xsl:apply-templates><xsl:with-param name="t" select="1" tunnel="yes"/></xsl:apply-templates></r></xsl:variable>
            <xsl:value-of select="count($r//*)"/>
          </xsl:template>
          <xsl:template match="*">
            <xsl:param name="t" tunnel="yes"/>
            <xsl:param name="u" tunnel="yes" select="0"/>
            <xsl:copy/>
            <xsl:apply-templates/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private const string Root = "<a xmlns='urn:a' xmlns:p='urn:p' xml:base='http://example.org/'>";

    // n elements: nested n deep, or all but the first children of the first.
    private static string Deep(int n)
        => Root + string.Concat(Enumerable.Repeat("<a>", n - 1)) + string.Concat(Enumerable.Repeat("</a>", n - 1)) + "</a>";

    private static string Wide(int n) => Root + string.Concat(Enumerable.Repeat("<a/>", n - 1)) + "</a>";

    [Fact]
    public async Task Ten_thousand_elements_nested_take_about_as_long_as_ten_thousand_elements_two_levels_deep()
    {
        var transformer = new XsltTransformer { MaxRecursionDepth = 2 * Elements };
        await transformer.LoadStylesheetAsync(Stylesheet);
        (await transformer.TransformAsync(Deep(500))).Trim().Should().Be("501"); // warm-up

        await CompareAsync("elements nested", () => transformer.TransformAsync(Deep(Elements)),
            "two levels deep", () => transformer.TransformAsync(Wide(Elements)), Elements + 1);
    }

    // An identity transform: in the deep input each level's copy holds all the levels below it in
    // the result too. Each level's content was cut out of the output buffer and written back after
    // its start tag, so it was copied once per level above it; and each element constructed looked
    // up the default namespace through every element open in the result.
    private const string Identity = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:template match="/"><xsl:variable name="c"><xsl:apply-templates/></xsl:variable><xsl:value-of select="count($c//*)"/></xsl:template>
          <xsl:template match="@*|node()"><xsl:copy><xsl:apply-templates select="@*|node()"/></xsl:copy></xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task An_identity_transform_of_ten_thousand_elements_nested_takes_about_as_long_as_of_ten_thousand_two_levels_deep()
    {
        var transformer = new XsltTransformer { MaxRecursionDepth = 2 * Elements };
        await transformer.LoadStylesheetAsync(Identity);
        (await transformer.TransformAsync(Deep(500))).Trim().Should().Be("500"); // warm-up

        await CompareAsync("elements nested", () => transformer.TransformAsync(Deep(Elements)),
            "two levels deep", () => transformer.TransformAsync(Wide(Elements)), Elements);
    }

    // A named template that makes one element per call: inside the element, the next call nests the
    // result one level deeper; after it, the call is a tail call and the result is flat.
    private static string NamedTemplate(bool nested) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xsl:output method="text"/>
          <xsl:param name="n" as="xs:integer"/>
          <xsl:template name="xsl:initial-template"><xsl:variable name="c"><xsl:call-template name="t"><xsl:with-param name="n" select="$n"/></xsl:call-template></xsl:variable><xsl:value-of select="count($c//*)"/></xsl:template>
          <xsl:template name="t">
            <xsl:param name="n"/>
            <xsl:if test="$n gt 0">
              {{(nested
                ? """<x><xsl:call-template name="t"><xsl:with-param name="n" select="$n - 1"/></xsl:call-template></x>"""
                : """<x/><xsl:call-template name="t"><xsl:with-param name="n" select="$n - 1"/></xsl:call-template>""")}}
            </xsl:if>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task A_named_template_nesting_ten_thousand_elements_takes_about_as_long_as_one_making_them_side_by_side()
    {
        var nested = new XsltTransformer { MaxRecursionDepth = 2 * Elements };
        await nested.LoadStylesheetAsync(NamedTemplate(nested: true));
        var flat = new XsltTransformer { MaxRecursionDepth = 2 * Elements };
        await flat.LoadStylesheetAsync(NamedTemplate(nested: false));
        foreach (var t in new[] { nested, flat })
        {
            t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            t.SetParameter("n", 500L);
            (await t.TransformAsync((string?)null)).Trim().Should().Be("500"); // warm-up
            t.SetParameter("n", (long)Elements);
        }

        await CompareAsync("elements nested", () => nested.TransformAsync((string?)null),
            "side by side", () => flat.TransformAsync((string?)null), Elements);
    }

    // Times the two transformations in turn, the best of five of each, without GC pauses, and
    // requires the deep one to take less than five times as long as the other.
    private async Task CompareAsync(string deepName, Func<Task<string>> deep, string wideName, Func<Task<string>> wide, int count)
    {
        var bestDeep = double.MaxValue;
        var bestWide = double.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            bestDeep = Math.Min(bestDeep, await TimeWithoutPausesAsync(deep, count));
            bestWide = Math.Min(bestWide, await TimeWithoutPausesAsync(wide, count));
        }

        var measured = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Elements:N0} {deepName} took {bestDeep:F0} ms and {wideName} {bestWide:F0} ms (without GC pauses, best of 5): ratio {bestDeep / bestWide:F2}");
        output.WriteLine(measured);
        (bestDeep / bestWide).Should().BeLessThan(5.0, measured);
    }

    private static async Task<double> TimeWithoutPausesAsync(Func<Task<string>> transform, int count)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var pausedBefore = GC.GetTotalPauseDuration();
        var watch = Stopwatch.StartNew();
        var result = await transform();
        watch.Stop();
        var paused = GC.GetTotalPauseDuration() - pausedBefore;
        result.Trim().Should().Be(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (watch.Elapsed - paused).TotalMilliseconds;
    }
}

[CollectionDefinition(nameof(DeepRecursionGrowthSerial), DisableParallelization = true)]
public sealed class DeepRecursionGrowthSerial;
