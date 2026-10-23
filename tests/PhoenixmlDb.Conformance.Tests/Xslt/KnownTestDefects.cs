namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// W3C cases whose expected result contradicts the XSLT 3.0 Recommendation, each with the
/// evidence. They still run and still count as failed, so the per-set numbers do not move; the
/// FAILED line carries a marker, and conformance.sh lists them in a KNOWN TEST DEFECTS block with
/// their count. An entry leaves when the suite corrects the case.
/// </summary>
public static class KnownTestDefects
{
    public static readonly IReadOnlyDictionary<string, string> Cases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // The unnamed mode is declared without use-accumulators, whose default is the empty list
        // (§6.6.1, §18.2.2), so no accumulator applies to the principal source or to a copy of
        // it (§18.2.2, §18.2.10): XTDE3362 is the spec result, and the test expects values.
        // Reported upstream as qt4cg/xslt40-test#13; the same defect in copy-3002 was corrected
        // by the suite editor (w3c/xslt30-test#50, copy-3003). Lucas, 2026-10-23.
        ["accumulator-073"] = "qt4cg/xslt40-test#13, w3c/xslt30-test#50",
    };

    /// <summary>The marker for a failed case's FAILED line: empty unless it is a known defect.</summary>
    public static string Marker(string caseName)
        => Cases.TryGetValue(caseName, out var reference) ? $"  [KNOWN TEST DEFECT — {reference}]" : "";
}
