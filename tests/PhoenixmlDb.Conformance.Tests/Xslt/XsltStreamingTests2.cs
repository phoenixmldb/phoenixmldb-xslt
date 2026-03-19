using FluentAssertions;
using Xunit;


namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// W3C XSLT 3.0 conformance tests for streaming instructions (strm/si-*).
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Suite", "XSLT")]
[Trait("Group", "strm")]
public class XsltStreamingTests2 : IClassFixture<XsltTestFixture>
{
    private readonly XsltTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public XsltStreamingTests2(XsltTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("tests/strm/si-apply-imports/_si-apply-imports-test-set.xml")]
    [InlineData("tests/strm/si-apply-templates/_si-apply-templates-test-set.xml")]
    [InlineData("tests/strm/si-assert/_si-assert-test-set.xml")]
    [InlineData("tests/strm/si-attribute/_si-attribute-test-set.xml")]
    [InlineData("tests/strm/si-call-template/_si-call-template-test-set.xml")]
    [InlineData("tests/strm/si-choose/_si-choose-test-set.xml")]
    [InlineData("tests/strm/si-copy/_si-copy-test-set.xml")]
    [InlineData("tests/strm/si-copy-of/_si-copy-of-test-set.xml")]
    [InlineData("tests/strm/si-document/_si-document-test-set.xml")]
    [InlineData("tests/strm/si-element/_si-element-test-set.xml")]
    [InlineData("tests/strm/si-for-each/_si-for-each-test-set.xml")]
    [InlineData("tests/strm/si-for-each-group/_si-for-each-group-test-set.xml")]
    [InlineData("tests/strm/si-fork/_si-fork-test-set.xml")]
    [InlineData("tests/strm/si-iterate/_si-iterate-test-set.xml")]
    [InlineData("tests/strm/si-LRE/_si-lre-test-set.xml")]
    [InlineData("tests/strm/si-map/_si-map-test-set.xml")]
    [InlineData("tests/strm/si-merge/_si-merge-test-set.xml")]
    [InlineData("tests/strm/si-message/_si-message-test-set.xml")]
    [InlineData("tests/strm/si-next-match/_si-next-match-test-set.xml")]
    [InlineData("tests/strm/si-on-empty/_si-on-empty-test-set.xml")]
    [InlineData("tests/strm/si-on-non-empty/_si-on-non-empty-test-set.xml")]
    [InlineData("tests/strm/si-perform-sort/_si-perform-sort-test-set.xml")]
    [InlineData("tests/strm/si-result-document/_si-result-document-test-set.xml")]
    [InlineData("tests/strm/si-try/_si-try-test-set.xml")]
    [InlineData("tests/strm/si-value-of/_si-value-of-test-set.xml")]
    [InlineData("tests/strm/si-where-populated/_si-where-populated-test-set.xml")]
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
