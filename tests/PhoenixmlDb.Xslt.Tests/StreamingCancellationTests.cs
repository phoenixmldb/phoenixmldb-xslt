using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression coverage for #187 — a long-running streamed transform must observe the
/// caller's <see cref="System.Threading.CancellationToken"/> and terminate promptly
/// instead of running to completion (or hanging).
///
/// Shape mirrors the W3C streaming cases sf-boolean-107 / sf-not-107: a large source
/// document driven through <c>xsl:source-document streamable="yes"</c> with a per-item
/// <c>xsl:for-each</c> body wrapped in <c>xsl:try</c>. Both the streaming forward pass
/// and the whole-input-buffer fallback (which the real strip-space + xsl:try case takes)
/// must poll the token so cancellation is honoured within one iteration.
/// </summary>
public class StreamingCancellationTests
{
    // ---- minimal streaming shape (no strip-space → stays on the streaming path) ----

    private const string StreamingStylesheet = """
        <?xml version="1.0" encoding="utf-8"?>
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
          <xsl:output method="xml"/>
          <xsl:template name="xsl:initial-template">
            <xsl:source-document streamable="yes" href="rows.xml">
              <out>
                <xsl:for-each select="account/transaction">
                  <t>
                    <xsl:try>
                      <xsl:value-of select="boolean(xs:double(concat('-', @value)))"/>
                      <xsl:catch errors="*:FORG0001" select="'invalid'"/>
                    </xsl:try>
                  </t>
                </xsl:for-each>
              </out>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    // Real sf-boolean-107 shape: strip-space forces the whole-input-buffer path, and
    // negative values make concat('-',@value) an invalid double (FORG0001 → caught).
    private const string BufferedStylesheet = """
        <?xml version="1.0" encoding="utf-8"?>
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
          <xsl:strip-space elements="*"/>
          <xsl:output method="xml"/>
          <xsl:template name="xsl:initial-template">
            <xsl:source-document streamable="yes" href="rows.xml">
              <out>
                <xsl:for-each select="account/transaction">
                  <t>
                    <xsl:try>
                      <xsl:value-of select="boolean(xs:double(concat('-', @value)))"/>
                      <xsl:catch errors="*:FORG0001" select="'invalid'"/>
                    </xsl:try>
                  </t>
                </xsl:for-each>
              </out>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private static string BuildRows(int n)
    {
        var sb = new StringBuilder(n * 40 + 64);
        sb.Append("<?xml version=\"1.0\"?>\n<account nr=\"1\">\n <account-number>1</account-number>\n");
        for (int i = 0; i < n; i++)
        {
            var v = (i % 3 == 0 ? "-" : "")
                + (((i % 50) + 0.11).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append("  <transaction value=\"").Append(v).Append("\" date=\"2007-01-01\"/>\n");
        }
        sb.Append("</account>\n");
        return sb.ToString();
    }

    private static async Task RunAsync(string stylesheet, int rows, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "rows.xml"), BuildRows(rows), ct);
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(stylesheet, new Uri(tempDir + "/"));
            transformer.SetInitialTemplate("xsl:initial-template", "http://www.w3.org/1999/XSL/Transform");
            await transformer.TransformAsync((string?)null, ct);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task StreamedTransform_AlreadyCancelledToken_ThrowsPromptly()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var sw = Stopwatch.StartNew();
        Func<Task> act = () => RunAsync(StreamingStylesheet, 100_000, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "an already-cancelled token must abort the streamed transform, not run 100K iterations");
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "cancellation must be observed almost immediately, not after full execution");
    }

    [Fact]
    public async Task StreamedTransform_TokenCancelledMidRun_ThrowsBeforeCompletion()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var sw = Stopwatch.StartNew();
        Func<Task> act = () => RunAsync(StreamingStylesheet, 200_000, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a token cancelled mid-run must interrupt the streaming loop");
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "the loop must poll the token and stop well before completing all 200K iterations");
    }

    [Fact]
    public async Task BufferedTransform_TokenCancelledMidRun_ThrowsBeforeCompletion()
    {
        // strip-space + xsl:try routes through the whole-input-buffer fallback; its
        // for-each loop must also observe cancellation.
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var sw = Stopwatch.StartNew();
        Func<Task> act = () => RunAsync(BufferedStylesheet, 200_000, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the buffered (whole-input) sf-boolean-107 shape must observe cancellation");
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "the buffered for-each loop must poll the token, not run to completion");
    }

    [Fact]
    public async Task StreamedTransform_HarnessShape_TaskRunWaitAsync_AbortsAtDeadline()
    {
        // Mirrors the conformance harness: transform inside Task.Run joined with
        // WaitAsync(cts.Token), deadline via CancelAfter.
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var sw = Stopwatch.StartNew();
        var transformTask = Task.Run(() => RunAsync(StreamingStylesheet, 200_000, cts.Token), cts.Token);
        Func<Task> act = async () => await transformTask.WaitAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "the harness-shaped Task.Run + WaitAsync join must observe the deadline");
    }
}
