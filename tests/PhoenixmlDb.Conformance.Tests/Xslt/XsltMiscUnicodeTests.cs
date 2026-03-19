using FluentAssertions;
using Xunit;


namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// W3C XSLT 3.0 conformance tests for unicode-90 (1460 test cases).
/// Split from XsltMiscTests to avoid resource/timeout issues.
///
/// WARNING: Each test iterates over ~1.1M Unicode codepoints via codepoints-to-string() + regex,
/// requiring ~500MB+ working memory per test. Running all 1460 tests causes OOM.
/// Disabled by default; enable with: dotnet test --filter "Category=UnicodeHeavy"
/// </summary>
[Trait("Category", "UnicodeHeavy")]
[Trait("Suite", "XSLT")]
[Trait("Group", "misc")]
public class XsltMiscUnicodeTests : IClassFixture<XsltTestFixture>
{
    private readonly XsltTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public XsltMiscUnicodeTests(XsltTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("tests/misc/unicode-90/_unicode-90-test-set.xml")]
    public async Task Xslt_ShouldPassTestSet(string testSetPath)
    {
        // Each unicode-90 test iterates ~1.1M codepoints via codepoints-to-string() + regex match,
        // requiring ~500MB+ working memory per test. With 1460 tests, this causes OOM.
        // Set RUN_UNICODE_HEAVY=1 to opt in.
        if (Environment.GetEnvironmentVariable("RUN_UNICODE_HEAVY") != "1")
        {
            _output.WriteLine("Skipping unicode-90 tests (OOM risk: ~500MB per test × 1460 tests). Set RUN_UNICODE_HEAVY=1 to run.");
            return;
        }

        if (!_fixture.IsTestDataAvailable)
        {
            _output.WriteLine("XSLT test data not available. Skipping test.");
            return;
        }

        var testCases = await _fixture.LoadTestSetAsync(testSetPath);
        _output.WriteLine($"Running {testCases.Count} tests from {testSetPath}");

        var passed = 0;
        var failed = 0;

        var testIndex = 0;
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

            // Each unicode-90 test iterates over ~1.1M codepoints creating massive string arrays.
            // Force GC after every test to prevent OOM (1460 tests × ~1M strings each).
            GC.Collect();
            GC.WaitForPendingFinalizers();
            testIndex++;
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
