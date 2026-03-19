using FluentAssertions;
using Xunit;


namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// W3C XSLT 3.0 conformance tests for instruction features (insn/*), part 2.
/// Split from XsltInstructionTests to avoid test process memory exhaustion.
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Suite", "XSLT")]
[Trait("Group", "insn")]
public class XsltInstructionTests2 : IClassFixture<XsltTestFixture>
{
    private readonly XsltTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public XsltInstructionTests2(XsltTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("tests/insn/analyze-string/_analyze-string-test-set.xml")]
    [InlineData("tests/insn/apply-imports/_apply-imports-test-set.xml")]
    [InlineData("tests/insn/apply-templates/_apply-templates-test-set.xml")]
    [InlineData("tests/insn/assert/_assert-test-set.xml")]
    [InlineData("tests/insn/attribute/_attribute-test-set.xml")]
    [InlineData("tests/insn/call-template/_call-template-test-set.xml")]
    [InlineData("tests/insn/choose/_choose-test-set.xml")]
    [InlineData("tests/insn/construct-node/_construct-node-test-set.xml")]
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
        passed.Should().BeGreaterThan(0, $"At least some tests in {testSetPath} should pass");
    }
}
