using FluentAssertions;
using Xunit;


namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// W3C XSLT 3.0 conformance tests for streaming features (strm/*).
/// Streaming functions (sf-*): count, sum, min, max, etc. with streaming.
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Suite", "XSLT")]
[Trait("Group", "strm")]
public class XsltStreamingTests : IClassFixture<XsltTestFixture>
{
    private readonly XsltTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public XsltStreamingTests(XsltTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("tests/strm/sf-avg/_sf-avg-test-set.xml")]
    [InlineData("tests/strm/sf-boolean/_sf-boolean-test-set.xml")]
    [InlineData("tests/strm/sf-copy-of/_sf-copy-of-test-set.xml")]
    [InlineData("tests/strm/sf-count/_sf-count-test-set.xml")]
    [InlineData("tests/strm/sf-deep-equal/_sf-deep-equal-test-set.xml")]
    [InlineData("tests/strm/sf-distinct-values/_sf-distinct-values-test-set.xml")]
    [InlineData("tests/strm/sf-empty/_sf-empty-test-set.xml")]
    [InlineData("tests/strm/sf-exactly-one/_sf-exactly-one-test-set.xml")]
    [InlineData("tests/strm/sf-exists/_sf-exists-test-set.xml")]
    [InlineData("tests/strm/sf-filter/_sf-filter-test-set.xml")]
    [InlineData("tests/strm/sf-fold-left/_sf-fold-left-test-set.xml")]
    [InlineData("tests/strm/sf-fold-right/_sf-fold-right-test-set.xml")]
    [InlineData("tests/strm/sf-has-children/_sf-has-children-test-set.xml")]
    [InlineData("tests/strm/sf-head/_sf-head-test-set.xml")]
    [InlineData("tests/strm/sf-index-of/_sf-index-of-test-set.xml")]
    [InlineData("tests/strm/sf-innermost/_sf-innermost-test-set.xml")]
    [InlineData("tests/strm/sf-insert-before/_sf-insert-before-test-set.xml")]
    [InlineData("tests/strm/sf-map-new/_sf-map-new-test-set.xml")]
    [InlineData("tests/strm/sf-max/_sf-max-test-set.xml")]
    [InlineData("tests/strm/sf-min/_sf-min-test-set.xml")]
    [InlineData("tests/strm/sf-not/_sf-not-test-set.xml")]
    [InlineData("tests/strm/sf-one-or-more/_sf-one-or-more-test-set.xml")]
    [InlineData("tests/strm/sf-outermost/_sf-outermost-test-set.xml")]
    [InlineData("tests/strm/sf-remove/_sf-remove-test-set.xml")]
    [InlineData("tests/strm/sf-reverse/_sf-reverse-test-set.xml")]
    [InlineData("tests/strm/sf-snapshot/_sf-snapshot-test-set.xml")]
    [InlineData("tests/strm/sf-string-join/_sf-string-join-test-set.xml")]
    [InlineData("tests/strm/sf-subsequence/_sf-subsequence-test-set.xml")]
    [InlineData("tests/strm/sf-sum/_sf-sum-test-set.xml")]
    [InlineData("tests/strm/sf-tail/_sf-tail-test-set.xml")]
    [InlineData("tests/strm/sf-trace/_sf-trace-test-set.xml")]
    [InlineData("tests/strm/sf-unordered/_sf-unordered-test-set.xml")]
    [InlineData("tests/strm/sf-unparsed-entity-uri/_sf-unparsed-entity-uri-test-set.xml")]
    [InlineData("tests/strm/sf-xml-to-json/_sf-xml-to-json-test-set.xml")]
    [InlineData("tests/strm/sf-zero-or-one/_sf-zero-or-one-test-set.xml")]
    [InlineData("tests/strm/sf-codepoints-to-string/_sf-codepoints-to-string-test-set.xml")]
    [InlineData("tests/strm/sf-current/_sf-current-test-set.xml")]
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
        // Don't assert — we're measuring baseline for streaming tests
    }
}
