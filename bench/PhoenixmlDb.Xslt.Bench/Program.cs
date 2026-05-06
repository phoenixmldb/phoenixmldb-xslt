using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using PhoenixmlDb.Xslt.Bench;

// Two run modes:
//   no args → BenchmarkDotNet's default runner (formal benchmark, summary table)
//   "smoke" → quick run that wires up + runs each benchmark once at 1 MB without
//             BenchmarkDotNet, just to verify the synthesizer/code path works.
//             Useful for CI smoke-testing without pulling in a 5-minute bench run.

if (args.Length > 0 && args[0] == "smoke")
{
    var sizeMb = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 1;
    Console.WriteLine($"Smoke test: {sizeMb} MB benchmark of all three transforms");
    var b = new XsltLargeDocumentBenchmarks { SizeMb = sizeMb };
    b.Setup();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var identity = await b.IdentityCopy();
    sw.Stop();
    Console.WriteLine($"  IdentityCopy:    {sw.Elapsed.TotalMilliseconds,8:F1} ms  output={identity.Length:N0} chars");

    sw.Restart();
    var wrapped = await b.WrapEachEntity();
    sw.Stop();
    Console.WriteLine($"  WrapEachEntity:  {sw.Elapsed.TotalMilliseconds,8:F1} ms  output={wrapped.Length:N0} chars");

    sw.Restart();
    var report = await b.ProjectToReport();
    sw.Stop();
    Console.WriteLine($"  ProjectToReport: {sw.Elapsed.TotalMilliseconds,8:F1} ms  output={report.Length:N0} chars");
    return 0;
}

// Default: real BenchmarkDotNet run. Default summary style is fine — milliseconds is
// already the natural unit for these durations.
var config = ManualConfig.CreateMinimumViable()
    .AddLogger(ConsoleLogger.Default);
BenchmarkRunner.Run<XsltLargeDocumentBenchmarks>(config);
return 0;
