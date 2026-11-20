using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using PhoenixmlDb.Xslt.Bench;

// Two run modes:
//   no args → BenchmarkDotNet's default runner (formal benchmark, summary table)
//   "smoke" → quick run that wires up + runs each benchmark once at 1 MB without
//             BenchmarkDotNet, just to verify the synthesizer/code path works.
//             Useful for CI smoke-testing without pulling in a 5-minute bench run.

// "streaming-identity" → measure peak working-set delta for a streamed
// identity transform on a synthetic N-item document. Headline metric for
// the streaming output drain work (see RELEASES.md unreleased section).
//   args: streaming-identity [itemCount]
if (args.Length > 0 && args[0] == "streaming-identity")
{
    var count = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 1_000_000;
    await StreamingIdentityBench.RunAsync(count);
    return 0;
}

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

// "micro" → run a single benchmark N times in-process, report min/median/p90/max.
// Useful for A/B comparing perf changes when smoke-test variance is too high.
//   args: micro <name> [iterations] [sizeMb]
//     name = identity | wrap | report
if (args.Length > 0 && args[0] == "micro")
{
    var which = args.Length > 1 ? args[1] : "report";
    var iterations = args.Length > 2 && int.TryParse(args[2], out var it) ? it : 30;
    var sizeMb = args.Length > 3 && int.TryParse(args[3], out var sm) ? sm : 10;
    Console.WriteLine($"Micro: {which} × {iterations} iterations at {sizeMb} MB");
    var b = new XsltLargeDocumentBenchmarks { SizeMb = sizeMb };
    b.Setup();

    Func<Task<string>> bench = which switch
    {
        "identity" => () => b.IdentityCopy(),
        "wrap" => () => b.WrapEachEntity(),
        _ => () => b.ProjectToReport()
    };

    // Warm-up: 3 iterations to stabilize JIT and caches
    for (var w = 0; w < 3; w++) await bench();

    var samples = new double[iterations];
    var sw2 = new System.Diagnostics.Stopwatch();
    for (var i = 0; i < iterations; i++)
    {
        sw2.Restart();
        var s = await bench();
        sw2.Stop();
        samples[i] = sw2.Elapsed.TotalMilliseconds;
        if (i == 0) Console.WriteLine($"  output: {s.Length:N0} chars");
    }
    Array.Sort(samples);
    Console.WriteLine($"  min={samples[0]:F1} med={samples[iterations / 2]:F1} p90={samples[iterations * 9 / 10]:F1} max={samples[iterations - 1]:F1} (ms)");
    Console.WriteLine($"  mean={samples.Average():F1} stddev={Math.Sqrt(samples.Select(x => (x - samples.Average()) * (x - samples.Average())).Sum() / iterations):F1}");
    return 0;
}

// "apply-probe" → each ApplyTimeBenchmarks parameter set once, outside BenchmarkDotNet: checks that
// the generated stylesheet runs, and shows how long the formal run will take.
if (args.Length > 0 && args[0] == "apply-probe")
{
    // apply-probe [sizeKb] [modes] [runs]: one size and mode count, each rule count, several runs.
    var sizes = args.Length > 1 ? [int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)] : new[] { 5, 100, 1024, 5120 };
    var modeCounts = args.Length > 2 ? [int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)] : new[] { 1, 10 };
    var runs = args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 1;
    foreach (var modes in modeCounts)
    foreach (var rules in new[] { 5, 50, 400 })
    foreach (var sizeKb in sizes)
    {
        var b = new ApplyTimeBenchmarks { Modes = modes, Rules = rules, SizeKb = sizeKb };
        var setup = System.Diagnostics.Stopwatch.StartNew();
        b.Setup();
        setup.Stop();
        for (var r = 0; r < runs; r++)
        {
            var a0 = GC.GetTotalAllocatedBytes(precise: true);
            var run = System.Diagnostics.Stopwatch.StartNew();
            var output = await b.Apply();
            run.Stop();
            var alloc = GC.GetTotalAllocatedBytes(precise: true) - a0;
            Console.WriteLine($"Modes={modes,2} Rules={rules,3} SizeKb={sizeKb,4}: setup {setup.Elapsed.TotalMilliseconds,8:F0} ms, apply {run.Elapsed.TotalMilliseconds,8:F1} ms, alloc {alloc:N0} B, output {output.Length:N0} chars");
        }
    }
    return 0;
}

// "apply" → ApplyTimeBenchmarks: the time to apply a compiled, validator-shaped stylesheet.
// It runs in-process, on the runtime of this build (run the net8.0 and net10.0 builds in turn),
// so that BenchmarkDotNet does not generate and rebuild a child project. The remaining
// arguments go to BenchmarkDotNet (--filter, --artifacts, --job dry ...).
if (args.Length > 0 && args[0] == "apply")
{
    var applyConfig = ManualConfig.CreateMinimumViable()
        .AddLogger(ConsoleLogger.Default)
        .AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default)
        .AddExporter(BenchmarkDotNet.Exporters.Csv.CsvExporter.Default)
        .AddExporter(BenchmarkDotNet.Exporters.MarkdownExporter.GitHub)
        .AddJob(BenchmarkDotNet.Jobs.Job.Default
            .WithToolchain(new BenchmarkDotNet.Toolchains.InProcess.Emit.InProcessEmitToolchain(TimeSpan.FromMinutes(30), logOutput: true))
            .WithStrategy(BenchmarkDotNet.Engines.RunStrategy.Monitoring)
            .WithLaunchCount(1).WithWarmupCount(2).WithIterationCount(7));
    BenchmarkSwitcher.FromTypes([typeof(ApplyTimeBenchmarks)]).Run(args[1..], applyConfig);
    return 0;
}

// Default: real BenchmarkDotNet run. Default summary style is fine — milliseconds is
// already the natural unit for these durations.
var config = ManualConfig.CreateMinimumViable()
    .AddLogger(ConsoleLogger.Default);
BenchmarkRunner.Run<XsltLargeDocumentBenchmarks>(config);
return 0;
