using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;

namespace PhoenixmlDb.Xslt.Bench;

/// <summary>
/// Wall-time + allocation benchmarks for representative XSLT transforms over
/// large (Dataverse-shaped) input documents.
/// </summary>
/// <remarks>
/// <para>
/// The three benchmarks exercise progressively heavier code paths:
/// </para>
/// <list type="bullet">
///   <item><c>IdentityCopy</c> — <c>shallow-copy</c> over the whole input. Measures the
///   raw cost of walking and re-emitting the source tree without any per-element work.
///   Dominated by source parsing + LRE serialization.</item>
///   <item><c>WrapEachEntity</c> — wraps each <c>xs:complexType</c> in a synthetic
///   container element via <c>xsl:for-each</c>. Many element constructors at the top
///   level → many save/restore cycles on <c>_output</c>.</item>
///   <item><c>ProjectToReport</c> — wraps each entity in a synthetic report element with
///   <c>xsl:variable as=...</c> bodies. This is the path that hits the
///   <c>_output.ToString()</c> save/restore pattern hardest — exactly what the Phase 1
///   refactor targets.</item>
/// </list>
/// <para>
/// All three run at three sizes (1 MB, 10 MB, 50 MB) so we can see scaling. 150 MB is
/// the documented target for Dataverse but is impractical to run inside BenchmarkDotNet
/// (the runner's own overhead dominates at that size). The 50 MB number scales linearly
/// to give us a good estimate.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3)]
public class XsltLargeDocumentBenchmarks
{
    private string _identityXsl = null!;
    private string _wrapXsl = null!;
    private string _reportXsl = null!;
    private string _sourceXml = null!;

    [Params(1, 10, 50)]
    public int SizeMb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Write the source doc to a per-size temp path; reused across all benchmarks of that size
        var path = Path.Combine(Path.GetTempPath(), $"phoenixmldb-bench-{SizeMb}mb.xml");
        if (!File.Exists(path) || new FileInfo(path).Length < SizeMb * 900_000L)
        {
            // Slightly oversize so the actual output is at-or-above the requested size after compression by serializer
            LargeDocumentSynthesizer.Synthesize(path, SizeMb * 1024L * 1024L);
        }
        _sourceXml = File.ReadAllText(path);

        _identityXsl = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode on-no-match="shallow-copy"/>
              <xsl:template match="/"><xsl:copy-of select="*"/></xsl:template>
            </xsl:stylesheet>
            """;

        _wrapXsl = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:template match="/">
                <wrapped>
                  <xsl:for-each select="//xs:complexType[@name]">
                    <entity name="{@name}">
                      <element-count><xsl:value-of select="count(xs:sequence/xs:element)"/></element-count>
                    </entity>
                  </xsl:for-each>
                </wrapped>
              </xsl:template>
            </xsl:stylesheet>
            """;

        _reportXsl = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:template match="/">
                <report>
                  <xsl:for-each select="//xs:complexType[@name]">
                    <xsl:variable name="entry" as="element(entry)">
                      <entry>
                        <xsl:attribute name="name" select="@name"/>
                        <xsl:attribute name="elements" select="count(xs:sequence/xs:element)"/>
                        <xsl:where-populated>
                          <xsl:attribute name="hasOwner"
                              select="exists(xs:sequence/xs:element[@name='Owner'])"/>
                        </xsl:where-populated>
                      </entry>
                    </xsl:variable>
                    <xsl:sequence select="$entry"/>
                  </xsl:for-each>
                </report>
              </xsl:template>
            </xsl:stylesheet>
            """;
    }

    [Benchmark(Description = "Identity copy (shallow-copy mode)")]
    public async Task<string> IdentityCopy()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(_identityXsl).ConfigureAwait(false);
        return await t.TransformAsync(_sourceXml).ConfigureAwait(false);
    }

    [Benchmark(Description = "Wrap each entity (xsl:for-each + LRE constructors)")]
    public async Task<string> WrapEachEntity()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(_wrapXsl).ConfigureAwait(false);
        return await t.TransformAsync(_sourceXml).ConfigureAwait(false);
    }

    [Benchmark(Description = "Project to report (xsl:variable as= + where-populated)")]
    public async Task<string> ProjectToReport()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(_reportXsl).ConfigureAwait(false);
        return await t.TransformAsync(_sourceXml).ConfigureAwait(false);
    }
}
