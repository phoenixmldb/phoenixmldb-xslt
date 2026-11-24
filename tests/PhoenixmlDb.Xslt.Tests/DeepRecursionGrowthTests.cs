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
        var (deep, wide) = (Deep(Elements), Wide(Elements));
        (await transformer.TransformAsync(Deep(500))).Trim().Should().Be("501"); // warm-up

        var bestDeep = double.MaxValue;
        var bestWide = double.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            bestDeep = Math.Min(bestDeep, await TimeWithoutPausesAsync(transformer, deep));
            bestWide = Math.Min(bestWide, await TimeWithoutPausesAsync(transformer, wide));
        }

        var measured = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Elements:N0} elements nested took {bestDeep:F0} ms and two levels deep {bestWide:F0} ms (without GC pauses, best of 5): ratio {bestDeep / bestWide:F2}");
        output.WriteLine(measured);
        (bestDeep / bestWide).Should().BeLessThan(5.0, measured);
    }

    private static async Task<double> TimeWithoutPausesAsync(XsltTransformer transformer, string source)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var pausedBefore = GC.GetTotalPauseDuration();
        var watch = Stopwatch.StartNew();
        var result = await transformer.TransformAsync(source);
        watch.Stop();
        var paused = GC.GetTotalPauseDuration() - pausedBefore;
        result.Trim().Should().Be((Elements + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (watch.Elapsed - paused).TotalMilliseconds;
    }
}

[CollectionDefinition(nameof(DeepRecursionGrowthSerial), DisableParallelization = true)]
public sealed class DeepRecursionGrowthSerial;
