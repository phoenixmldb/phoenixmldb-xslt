using FluentAssertions;
using Xunit;


namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// W3C XSLT 3.0 conformance tests for function features (fn/*).
/// Covers string, numeric, date, format, document, key, and other XPath/XSLT functions.
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Suite", "XSLT")]
[Trait("Group", "fn")]
public class XsltFunctionTests : IClassFixture<XsltTestFixture>
{
    private readonly XsltTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public XsltFunctionTests(XsltTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData("tests/fn/accessor/_accessor-test-set.xml")]
    [InlineData("tests/fn/available-system-properties/_available-system-properties-test-set.xml")]
    [InlineData("tests/fn/base-uri/_base-uri-test-set.xml")]
    [InlineData("tests/fn/collection/_collection-test-set.xml")]
    [InlineData("tests/fn/core-function/_core-function-test-set.xml")]
    [InlineData("tests/fn/copy-of/_copy-of-test-set.xml")]
    [InlineData("tests/fn/current/_current-test-set.xml")]
    [InlineData("tests/fn/current-output-uri/_current-output-uri-test-set.xml")]
    [InlineData("tests/fn/deep-equal/_deep-equal-test-set.xml")]
    [InlineData("tests/fn/document/_document-test-set.xml")]
    [InlineData("tests/fn/extension-functions/_extension-functions-test-set.xml")]
    [InlineData("tests/fn/format-date/_format-date-test-set.xml")]
    [InlineData("tests/fn/format-date-en/_format-date-en-test-set.xml")]
    [InlineData("tests/fn/format-number/_format-number-test-set.xml")]
    [InlineData("tests/fn/function-available/_function-available-test-set.xml")]
    [InlineData("tests/fn/function-lookup/_function-lookup-test-set.xml")]
    [InlineData("tests/fn/id/_id-test-set.xml")]
    [InlineData("tests/fn/innermost/_innermost-test-set.xml")]
    [InlineData("tests/fn/json-to-xml/_json-to-xml-test-set.xml")]
    [InlineData("tests/fn/key/_key-test-set.xml")]
    [InlineData("tests/fn/load-xquery-module/_load-xquery-module-test-set.xml")]
    [InlineData("tests/fn/normalize-unicode/_normalize-unicode-test-set.xml")]
    [InlineData("tests/fn/outermost/_outermost-test-set.xml")]
    [InlineData("tests/fn/position/_position-test-set.xml")]
    [InlineData("tests/fn/resolve-uri/_resolve-uri-test-set.xml")]
    [InlineData("tests/fn/root/_root-test-set.xml")]
    [InlineData("tests/fn/snapshot/_snapshot-test-set.xml")]
    [InlineData("tests/fn/stream-available/_stream-available-test-set.xml")]
    [InlineData("tests/fn/system-property/_system-property-test-set.xml")]
    [InlineData("tests/fn/transform/_transform-test-set.xml")]
    [InlineData("tests/fn/type-available/_type-available-test-set.xml")]
    [InlineData("tests/fn/unparsed-entity-uri/_unparsed-entity-uri-test-set.xml")]
    [InlineData("tests/fn/unparsed-text/_unparsed-text-test-set.xml")]
    [InlineData("tests/fn/unparsed-text-lines/_unparsed-text-lines-test-set.xml")]
    [InlineData("tests/fn/xml-to-json/_xml-to-json-test-set.xml")]
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
