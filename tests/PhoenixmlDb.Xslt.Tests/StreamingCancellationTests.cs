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
                  <xsl:message>.</xsl:message>
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
                  <xsl:message>.</xsl:message>
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

    /// <summary>
    /// Runs the transform, counting ITEMS PROCESSED via xsl:message — one per for-each iteration,
    /// dispatched by the same loop body whose cancellation is under test. <paramref name="onItem"/>
    /// is called with the running count, which is how the mid-run tests cancel: on observed
    /// progress, never on a timer.
    /// </summary>
    private static async Task<int> RunAsync(string stylesheet, int rows, CancellationToken ct,
        Action<int>? onItem = null)
    {
        var processed = 0;
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "rows.xml"), BuildRows(rows), ct);
            var transformer = new XsltTransformer
            {
                MessageListener = (_, _) => onItem?.Invoke(Interlocked.Increment(ref processed)),
            };
            await transformer.LoadStylesheetAsync(stylesheet, new Uri(tempDir + "/"));
            transformer.SetInitialTemplate("xsl:initial-template", "http://www.w3.org/1999/XSL/Transform");
            await transformer.TransformAsync((string?)null, ct);
            return processed;
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private const int Rows = 400_000;

    /// <summary>Cancel once this many items have actually been processed.</summary>
    private const int CancelAfterItems = 2_000;

    /// <summary>
    /// A loop that stopped has processed far fewer than all the items. The margin is enormous on
    /// purpose: the claim is "it stopped", not "it stopped within N iterations", so no plausible
    /// amount of in-flight buffering can carry it over.
    /// </summary>
    private const int MustBeBelow = Rows / 4;

    /// <summary>
    /// Counts items and cancels at the threshold, returning how many were processed in total.
    /// </summary>
    /// <remarks>
    /// The count is kept HERE, not taken from RunAsync's return value. RunAsync throws on
    /// cancellation — which is the whole point — so its return value is never assigned, and reading
    /// the count from it yields 0 on every run. A 0 would then satisfy "fewer than a quarter of the
    /// items" and the test would pass while measuring nothing. The lower-bound assertion in each
    /// test is what caught that, and is why it is there.
    /// </remarks>
    private static async Task<int> RunCancellingOnProgressAsync(string stylesheet, CancellationTokenSource cts)
    {
        var processed = 0;
        try
        {
            await RunAsync(stylesheet, Rows, cts.Token, n =>
            {
                processed = n;
                if (n >= CancelAfterItems) cts.Cancel();
            });
        }
        catch (OperationCanceledException)
        {
            // Expected: the count, not the exception, is the assertion.
        }
        return processed;
    }

    [Fact]
    public async Task StreamedTransform_AlreadyCancelledToken_ProcessesNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var processed = 0;
        Func<Task> act = () => RunAsync(StreamingStylesheet, Rows, cts.Token, n => processed = n);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "an already-cancelled token must abort the streamed transform");
        processed.Should().Be(0,
            "not a single for-each iteration may run when the token is cancelled before the start");
    }

    [Fact]
    public async Task StreamedTransform_CancelledOnProgress_StopsBeforeConsumingTheInput()
    {
        using var cts = new CancellationTokenSource();

        var processed = await RunCancellingOnProgressAsync(StreamingStylesheet, cts);

        processed.Should().BeGreaterThanOrEqualTo(CancelAfterItems,
            "the loop must actually have been running when the token was cancelled — "
            + "otherwise this asserts nothing about cancellation");
        processed.Should().BeLessThan(MustBeBelow,
            $"the streaming loop must poll the token and stop, not run all {Rows} iterations");
    }

    [Fact]
    public async Task BufferedTransform_CancelledOnProgress_StopsBeforeConsumingTheInput()
    {
        // strip-space + xsl:try routes through the whole-input-buffer fallback; its
        // for-each loop must also observe cancellation.
        using var cts = new CancellationTokenSource();

        var processed = await RunCancellingOnProgressAsync(BufferedStylesheet, cts);

        processed.Should().BeGreaterThanOrEqualTo(CancelAfterItems);
        processed.Should().BeLessThan(MustBeBelow,
            "the buffered (whole-input) sf-boolean-107 loop must poll the token, not run to completion");
    }

    [Fact]
    public async Task StreamedTransform_HarnessShape_TaskRunWaitAsync_StopsBeforeConsumingTheInput()
    {
        // Mirrors the conformance harness: transform inside Task.Run joined with WaitAsync.
        using var cts = new CancellationTokenSource();

        var processed = 0;
        var transformTask = Task.Run(async () =>
        {
            try
            {
                await RunAsync(StreamingStylesheet, Rows, cts.Token, n =>
                {
                    processed = n;
                    if (n >= CancelAfterItems) cts.Cancel();
                });
            }
            catch (OperationCanceledException) { /* expected */ }
        }, CancellationToken.None);

        await transformTask;

        processed.Should().BeLessThan(MustBeBelow,
            "the harness-shaped Task.Run join must still stop the loop");
    }
}
