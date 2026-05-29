using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using PhoenixmlDb.Xslt;

namespace PhoenixmlDb.Xslt.Bench;

/// <summary>
/// Quick in-process check of streaming-identity peak memory. Not a BenchmarkDotNet
/// benchmark — just a console measurement so the team can verify peak RSS without
/// hand-running the CLI on a multi-GB file.
/// </summary>
public static class StreamingIdentityBench
{
    public static async Task RunAsync(int itemCount = 1_000_000)
    {
        var tmpInput = Path.Combine(Path.GetTempPath(),
            $"strm-bench-{Guid.NewGuid():N}.xml");
        var tmpOutput = Path.Combine(Path.GetTempPath(),
            $"strm-bench-{Guid.NewGuid():N}.out.xml");

        try
        {
            await WriteItemsDocumentAsync(tmpInput, itemCount);

            var stylesheet = """
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:mode streamable="yes" on-no-match="shallow-copy"/>
                  <xsl:output method="xml" indent="no"/>
                </xsl:stylesheet>
                """;

            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(stylesheet);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocBefore = GC.GetTotalAllocatedBytes();
            var peakBefore = Process.GetCurrentProcess().PeakWorkingSet64;

            var sw = Stopwatch.StartNew();
            using (var inStream = File.OpenRead(tmpInput))
            using (var outStream = File.Create(tmpOutput))
            {
                await transformer.TransformAsync(inStream, outStream);
            }
            sw.Stop();

            var allocAfter = GC.GetTotalAllocatedBytes();
            var peakAfter = Process.GetCurrentProcess().PeakWorkingSet64;
            var alloc = allocAfter - allocBefore;
            var peakDelta = peakAfter - peakBefore;
            var outputSize = new FileInfo(tmpOutput).Length;

            Console.WriteLine($"items:    {itemCount:N0}");
            Console.WriteLine($"elapsed:  {sw.ElapsedMilliseconds:N0} ms");
            Console.WriteLine($"alloc:    {alloc / (1024.0 * 1024.0):F1} MiB");
            Console.WriteLine($"peakRSSΔ: {peakDelta / (1024.0 * 1024.0):F1} MiB");
            Console.WriteLine($"output:   {outputSize / (1024.0 * 1024.0):F1} MiB");
        }
        finally
        {
            if (File.Exists(tmpInput)) File.Delete(tmpInput);
            if (File.Exists(tmpOutput)) File.Delete(tmpOutput);
        }
    }

    private static async Task WriteItemsDocumentAsync(string path, int n)
    {
        await using var fs = File.Create(path);
        await using var w = new StreamWriter(fs, Encoding.UTF8);
        await w.WriteAsync("<items>");
        for (int i = 0; i < n; i++)
        {
            await w.WriteAsync("<item id=\"");
            await w.WriteAsync(i.ToString(CultureInfo.InvariantCulture));
            await w.WriteAsync("\">v");
            await w.WriteAsync(i.ToString(CultureInfo.InvariantCulture));
            await w.WriteAsync("</item>");
        }
        await w.WriteAsync("</items>");
    }
}
