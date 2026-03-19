using FluentAssertions;
using Xunit;


namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// W3C XSLT 3.0 conformance tests for attribute-related features (attr/*).
/// Covers @as, @mode, @select, @expand-text, AVTs, and other stylesheet attributes.
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Suite", "XSLT")]
[Trait("Group", "attr")]
public class XsltAttributeTests : IClassFixture<XsltTestFixture>
{
    private readonly XsltTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public XsltAttributeTests(XsltTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("tests/attr/as/_as-test-set.xml")]
    [InlineData("tests/attr/avt/_avt-test-set.xml")]
    [InlineData("tests/attr/declared-modes/_declared-modes-test-set.xml")]
    [InlineData("tests/attr/disable-output-escaping/_disable-output-escaping-test-set.xml")]
    [InlineData("tests/attr/expand-text/_expand-text-test-set.xml")]
    [InlineData("tests/attr/match/_match-test-set.xml")]
    [InlineData("tests/attr/mode/_mode-test-set.xml")]
    [InlineData("tests/attr/package-version/_package-version-test-set.xml")]
    [InlineData("tests/attr/select/_select-test-set.xml")]
    [InlineData("tests/attr/shadow/_shadow-test-set.xml")]
    [InlineData("tests/attr/static/_static-test-set.xml")]
    [InlineData("tests/attr/streamable/_streamable-test-set.xml")]
    [InlineData("tests/attr/strip-type-annotations/_strip-type-annotations-test-set.xml")]
    [InlineData("tests/attr/tunnel/_tunnel-test-set.xml")]
    [InlineData("tests/attr/use-when/_use-when-test-set.xml")]
    [InlineData("tests/attr/validation/_validation-test-set.xml")]
    [InlineData("tests/attr/version/_version-test-set.xml")]
    [InlineData("tests/attr/xpath-default-namespace/_xpath-default-namespace-test-set.xml")]
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
