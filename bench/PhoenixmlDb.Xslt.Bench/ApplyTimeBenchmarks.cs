using BenchmarkDotNet.Attributes;

namespace PhoenixmlDb.Xslt.Bench;

/// <summary>
/// The time to apply a compiled stylesheet, without the compile: the stylesheet is loaded once
/// in <see cref="Setup"/>, and the benchmark measures <see cref="XsltTransformer.TransformAsync(string?, CancellationToken)"/>
/// only. The other benchmarks here load the stylesheet inside the measured method, so a change
/// to the apply path is hidden by the compile time. The stylesheet is shaped like a compiled
/// Schematron validator (see <see cref="ValidatorShapeSynthesizer"/>).
/// </summary>
/// <remarks>Run with <c>apply</c> (see Program.cs), which sets the job: in-process, monitoring, 2 warmups, 7 iterations.</remarks>
public class ApplyTimeBenchmarks
{
    private XsltTransformer _transformer = null!;
    private string _input = null!;

    /// <summary>Modes (Schematron patterns).</summary>
    [Params(1, 10)]
    public int Modes { get; set; }

    /// <summary>Rules per mode.</summary>
    [Params(5, 50, 400)]
    public int Rules { get; set; }

    /// <summary>Input size in KB: 5 is the warm small-payload case.</summary>
    /// <remarks>5120 (5 MB) is run separately (see Program.cs, apply-probe), not under BenchmarkDotNet's schedule.</remarks>
    [Params(5, 100, 1024)]
    public int SizeKb { get; set; }

    public int Asserts { get; set; } = 3;

    [GlobalSetup]
    public void Setup()
    {
        _transformer = new XsltTransformer();
        _transformer.LoadStylesheetAsync(ValidatorShapeSynthesizer.Stylesheet(Modes, Rules, Asserts)).GetAwaiter().GetResult();
        _input = ValidatorShapeSynthesizer.Input(Rules, SizeKb * 1024);
        // One transform outside the measurement, so that a first-use cost is not in the result.
        _transformer.TransformAsync(_input).GetAwaiter().GetResult();
    }

    [Benchmark]
    public Task<string> Apply() => _transformer.TransformAsync(_input);
}
