using FluentAssertions;
using Xunit;


namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// W3C XSLT 3.0 conformance tests for miscellaneous features (misc/*).
/// Covers backwards compatibility, regex, whitespace, built-in templates, etc.
/// Large test sets (error, regex-syntax, regex-syntax-xslt20, unicode-90) are in separate classes.
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Suite", "XSLT")]
[Trait("Group", "misc")]
public class XsltMiscTests : IClassFixture<XsltTestFixture>
{
    private readonly XsltTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public XsltMiscTests(XsltTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("tests/misc/aspiring/_aspiring-test-set.xml")]
    [InlineData("tests/misc/backwards/_backwards-test-set.xml")]
    [InlineData("tests/misc/bug/_bug-test-set.xml")]
    [InlineData("tests/misc/built-in-templates/_built-in-templates-test-set.xml")]
    [InlineData("tests/misc/catalog/_catalog-test-set.xml")]
    [InlineData("tests/misc/collations/_collations-test-set.xml")]
    [InlineData("tests/misc/docbook/_docbook-test-set.xml")]
    [InlineData("tests/misc/embedded-stylesheet/_embedded-stylesheet-test-set.xml")]
    [InlineData("tests/misc/forwards/_forwards-test-set.xml")]
    [InlineData("tests/misc/initial-function/_initial-function-test-set.xml")]
    [InlineData("tests/misc/initial-mode/_initial-mode-test-set.xml")]
    [InlineData("tests/misc/initial-template/_initial-template-test-set.xml")]
    [InlineData("tests/misc/regex/_regex-test-set.xml")]
    // regex-classes excluded: each of 120 tests generates a 1.1M codepoint string and runs
    // regex analysis on it, causing OOM when timed-out zombie tasks accumulate memory.
    // [InlineData("tests/misc/regex-classes/_regex-classes-test-set.xml")]
    [InlineData("tests/misc/seqtor/_seqtor-test-set.xml")]
    [InlineData("tests/misc/streaming-fallback/_streaming-fallback-test-set.xml")]
    [InlineData("tests/misc/whitespace/_whitespace-test-set.xml")]
    [InlineData("tests/misc/xml-version/_xml-version-test-set.xml")]
    [InlineData("tests/misc/xslt-compat/_xslt-compat-test-set.xml")]
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
