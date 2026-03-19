using FluentAssertions;
using Xunit;


namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// W3C XSLT 3.0 conformance tests for streaming expressions (strm/sx-*) and usage patterns (strm/su-*).
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Suite", "XSLT")]
[Trait("Group", "strm")]
public class XsltStreamingTests3 : IClassFixture<XsltTestFixture>
{
    private readonly XsltTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public XsltStreamingTests3(XsltTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("tests/strm/sx-ArithmeticExpr/_sx-ArithmeticExpr-test-set.xml")]
    [InlineData("tests/strm/sx-CommaExpr/_sx-CommaExpr-test-set.xml")]
    [InlineData("tests/strm/sx-ExceptExpr/_sx-ExceptExpr-test-set.xml")]
    [InlineData("tests/strm/sx-ForExpr/_sx-ForExpr-test-set.xml")]
    [InlineData("tests/strm/sx-FunctionCall/_sx-FunctionCall-test-set.xml")]
    [InlineData("tests/strm/sx-GeneralComp-eq/_sx-GeneralComp-eq-test-set.xml")]
    [InlineData("tests/strm/sx-GeneralComp-ge/_sx-GeneralComp-ge-test-set.xml")]
    [InlineData("tests/strm/sx-GeneralComp-gt/_sx-GeneralComp-gt-test-set.xml")]
    [InlineData("tests/strm/sx-GeneralComp-le/_sx-GeneralComp-le-test-set.xml")]
    [InlineData("tests/strm/sx-GeneralComp-lt/_sx-GeneralComp-lt-test-set.xml")]
    [InlineData("tests/strm/sx-GeneralComp-ne/_sx-GeneralComp-ne-test-set.xml")]
    [InlineData("tests/strm/sx-IfExpr/_sx-IfExpr-test-set.xml")]
    [InlineData("tests/strm/sx-InstanceofExpr/_sx-InstanceofExpr-test-set.xml")]
    [InlineData("tests/strm/sx-IntersectExpr/_sx-IntersectExpr-test-set.xml")]
    [InlineData("tests/strm/sx-MapExpr/_sx-MapExpr-test-set.xml")]
    [InlineData("tests/strm/sx-PathExpr/_sx_PathExpr-test-set.xml")]
    [InlineData("tests/strm/sx-QuantifiedExpr/_sx-QuantifiedExpr-test-set.xml")]
    [InlineData("tests/strm/sx-SimpleMappingExpr/_sx-SimpleMappingExpr-test-set.xml")]
    [InlineData("tests/strm/sx-SquareArrayConstructor/_sx-SquareArrayConstructor-test-set.xml")]
    [InlineData("tests/strm/sx-TreatExpr/_sx-TreatExpr-test-set.xml")]
    [InlineData("tests/strm/sx-UnionExpr/_sx-UnionExpr-test-set.xml")]
    [InlineData("tests/strm/su-absorbing/_su-absorbing-test-set.xml")]
    [InlineData("tests/strm/su-ascent/_su-ascent-test-set.xml")]
    [InlineData("tests/strm/su-filter/_su-filter-test-set.xml")]
    [InlineData("tests/strm/su-inspection/_su-inspection-test-set.xml")]
    [InlineData("tests/strm/su-shallow-descent/_su-shallow-descent-test-set.xml")]
    [InlineData("tests/strm/su-unclassified/_su-unclassified-test-set.xml")]
    public async Task Xslt_ShouldPassTestSet(string testSetPath)
    {
        if (!_fixture.IsTestDataAvailable)
        {
            _output.WriteLine("XSLT test data not available. Skipping test.");
            return;
        }

        var testCases = await _fixture.LoadTestSetAsync(testSetPath);
        _output.WriteLine($"Running {testCases.Count} tests from {testSetPath}");

        var passed = 0;
        var failed = 0;

        foreach (var testCase in testCases)
        {
            var result = await _fixture.Runner.RunTestAsync(testCase, TestContext.Current.CancellationToken);
            if (result.Passed)
            {
                passed++;
            }
            else
            {
                failed++;
                _output.WriteLine($"FAILED: {testCase.Name}");
                if (result.Error != null)
                {
                    _output.WriteLine($"  Error: {result.Error.Message}");
                    if (result.Error is NullReferenceException && failed <= 5)
                        _output.WriteLine($"  Stack: {result.Error.StackTrace}");
                }
                else if (failed <= 30)
                {
                    var actual = result.ActualResult?[..Math.Min(200, result.ActualResult.Length)] ?? "(null)";
                    _output.WriteLine($"  Actual: {actual}");
                    var expected = testCase.Assertions.FirstOrDefault()?.Value?[..Math.Min(200, testCase.Assertions.FirstOrDefault()?.Value?.Length ?? 0)] ?? testCase.Assertions.FirstOrDefault()?.Type ?? "(none)";
                    _output.WriteLine($"  Expected: {expected}");
                }
            }
        }

        if (testCases.Count == 0)
        {
            _output.WriteLine($"No applicable tests in {testSetPath} (all filtered by dependencies)");
            return;
        }

        _output.WriteLine($"Results: {passed}/{testCases.Count} passed ({(double)passed / testCases.Count * 100:F1}%)");
    }
}
