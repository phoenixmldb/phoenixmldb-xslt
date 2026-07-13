using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression coverage for suppress-indentation with the HTML/XHTML output methods
/// (W3C decl/output-0725 [html] and output-0726 [xhtml]). A suppressed element's text
/// run must be emitted verbatim — no word-wrapping or indentation whitespace inserted
/// inside it — and, critically, the suppress-indentation post-processor must terminate:
/// a prior defect spun forever on the first text character inside a suppressed element,
/// hanging both cases. The performance guard locks in that the indenter/suppressor is
/// linear so a large paragraph serializes quickly.
/// </summary>
public class SuppressIndentationHtmlTests
{
    private static async Task<string> Transform(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync("<in/>");
    }

    private const string Lorem =
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor " +
        "incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud " +
        "exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat.";

    private static string Stylesheet(string method, string paragraph) => $@"<xsl:stylesheet version=""3.0"" xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"">
  <xsl:output method=""{method}"" html-version=""5"" indent=""yes"" suppress-indentation=""p""/>
  <xsl:template match=""/""><html><head><title>t</title></head><body><h1>t</h1><p>{paragraph}</p></body></html></xsl:template>
</xsl:stylesheet>";

    [Theory]
    [InlineData("html")]
    [InlineData("xhtml")]
    public async Task SuppressIndentation_EmitsParagraphVerbatim_OnOneLine(string method)
    {
        // The suppressed <p> must contain the text run with no inserted line breaks or
        // indentation whitespace — it appears verbatim on a single line (output-0725/0726).
        var result = await Transform(Stylesheet(method, Lorem));

        result.Should().Contain($"<p>{Lorem}</p>");
        // No newline may be inserted between <p> and </p> (that is the indentation the
        // serializer would otherwise add inside the paragraph, which suppress-indentation forbids).
        var pStart = result.IndexOf("<p>", StringComparison.Ordinal);
        var pEnd = result.IndexOf("</p>", pStart, StringComparison.Ordinal);
        pStart.Should().BeGreaterThan(-1);
        pEnd.Should().BeGreaterThan(pStart);
        result.Substring(pStart, pEnd - pStart).Should().NotContain("\n");
    }

    [Theory]
    [InlineData("html")]
    [InlineData("xhtml")]
    public async Task SuppressIndentation_LargeParagraph_SerializesQuickly(string method)
    {
        // Performance/liveness guard: a ~50k-character paragraph under suppress-indentation
        // must serialize well under a couple of seconds. A regression to the old non-terminating
        // (or super-linear) post-processor would blow past this instead of hanging the suite.
        var sb = new StringBuilder();
        while (sb.Length < 50_000)
            sb.Append(Lorem).Append(' ');
        var big = sb.ToString();

        var work = Transform(Stylesheet(method, big));
        var completed = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(work, "suppress-indentation serialization must terminate quickly, not hang");

        var sw = Stopwatch.StartNew();
        var result = await work;
        sw.Stop();
        sw.Elapsed.TotalSeconds.Should().BeLessThan(2.0);
        result.Should().Contain($"<p>{big}</p>");
    }
}
