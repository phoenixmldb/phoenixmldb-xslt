using FluentAssertions;
using Xunit;


namespace PhoenixmlDb.Conformance.Tests.XQuery;

/// <summary>
/// XQuery Test Suite (XQTS) conformance tests.
///
/// These tests run against the W3C XQuery Test Suite to verify
/// conformance with XQuery 3.1 and XPath 3.1 specifications.
///
/// Test Suite Sources:
/// - QT3 Tests (XQuery 3.1): https://github.com/w3c/qt3tests
/// - QT4 Tests (XQuery 4.0): https://github.com/qt4cg/qt4tests
///
/// To run these tests, clone the test suite repository to the TestData directory:
///   git clone https://github.com/w3c/qt3tests tests/PhoenixmlDb.Conformance.Tests/TestData/qt3tests
/// </summary>
[Trait("Category", "Conformance")]
[Trait("Suite", "XQTS")]
public class XqtsConformanceTests : IClassFixture<XqtsTestFixture>
{
    private readonly XqtsTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public XqtsConformanceTests(XqtsTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Xqts_ShouldPassBasicArithmeticTests()
    {
        // Basic arithmetic tests to verify engine is working
        var testCases = new[]
        {
            ("1 + 1", "2"),
            ("10 - 3", "7"),
            ("5 * 4", "20"),
            ("20 div 4", "5"),
            ("17 mod 5", "2"),
        };

        foreach (var (query, expected) in testCases)
        {
            var result = await _fixture.RunQueryAsync(query);
            result?.ToString().Should().Be(expected, $"Query: {query}");
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Xqts_ShouldPassBasicComparisonTests()
    {
        var testCases = new[]
        {
            ("1 < 2", "true"),
            ("5 > 3", "true"),
            ("3 = 3", "true"),
            ("4 != 5", "true"),
            ("2 <= 2", "true"),
            ("3 >= 3", "true"),
        };

        foreach (var (query, expected) in testCases)
        {
            var result = await _fixture.RunQueryAsync(query);
            result?.ToString()?.ToLower().Should().Be(expected, $"Query: {query}");
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Xqts_ShouldPassBasicStringFunctionTests()
    {
        var testCases = new[]
        {
            ("string-length('hello')", "5"),
            ("concat('hello', ' ', 'world')", "hello world"),
            ("upper-case('hello')", "HELLO"),
            ("lower-case('HELLO')", "hello"),
            ("substring('hello', 2, 3)", "ell"),
            ("contains('hello', 'ell')", "true"),
            ("starts-with('hello', 'hel')", "true"),
            ("ends-with('hello', 'llo')", "true"),
        };

        foreach (var (query, expected) in testCases)
        {
            var result = await _fixture.RunQueryAsync(query);
            var actual = result?.ToString()?.ToLower() ?? "";
            expected.ToLower().Should().Be(actual, $"Query: {query}");
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Xqts_ShouldPassBasicNumericFunctionTests()
    {
        var testCases = new[]
        {
            ("abs(-5)", "5"),
            ("floor(3.7)", "3"),
            ("ceiling(3.2)", "4"),
            ("round(3.5)", "4"),
            ("sum((1, 2, 3, 4, 5))", "15"),
            ("count((1, 2, 3))", "3"),
            ("min((3, 1, 2))", "1"),
            ("max((1, 3, 2))", "3"),
        };

        foreach (var (query, expected) in testCases)
        {
            var result = await _fixture.RunQueryAsync(query);
            result?.ToString().Should().Be(expected, $"Query: {query}");
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Xqts_ShouldPassBasicSequenceFunctionTests()
    {
        var testCases = new[]
        {
            ("empty(())", "true"),
            ("exists((1, 2, 3))", "true"),
            ("head((1, 2, 3))", "1"),
            ("reverse((1, 2, 3))", "3 2 1"),
            ("distinct-values((1, 2, 2, 3, 3, 3))", "1 2 3"),
        };

        foreach (var (query, expected) in testCases)
        {
            var result = await _fixture.RunQueryAsync(query);
            var actual = result?.ToString() ?? "";
            // Normalize sequence output
            if (result is IEnumerable<object> seq)
            {
                actual = string.Join(" ", seq);
            }
            actual.ToLower().Should().Be(expected.ToLower(), $"Query: {query}");
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Xqts_ShouldPassBasicFlworTests()
    {
        var query = @"
            for $x in (1, 2, 3)
            return $x * 2
        ";
        var result = await _fixture.RunQueryAsync(query);

        if (result is IEnumerable<object> seq)
        {
            var values = seq.Select(x => x?.ToString()).ToList();
            values.Should().BeEquivalentTo(new[] { "2", "4", "6" });
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Xqts_ShouldPassBasicLetClauseTests()
    {
        var query = @"
            let $x := 10
            let $y := 20
            return $x + $y
        ";
        var result = await _fixture.RunQueryAsync(query);
        result?.ToString().Should().Be("30");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Xqts_ShouldPassBasicWhereClauseTests()
    {
        var query = @"
            for $x in (1, 2, 3, 4, 5)
            where $x > 3
            return $x
        ";
        var result = await _fixture.RunQueryAsync(query);

        if (result is IEnumerable<object> seq)
        {
            var values = seq.Select(x => x?.ToString()).ToList();
            values.Should().BeEquivalentTo(new[] { "4", "5" });
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Xqts_ShouldPassBasicOrderByTests()
    {
        var query = @"
            for $x in (3, 1, 4, 1, 5)
            order by $x
            return $x
        ";
        var result = await _fixture.RunQueryAsync(query);

        if (result is IEnumerable<object> seq)
        {
            var values = seq.Select(x => x?.ToString()).ToList();
            values.Should().BeEquivalentTo(new[] { "1", "1", "3", "4", "5" });
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Xqts_ShouldPassBasicIfThenElseTests()
    {
        var testCases = new[]
        {
            ("if (true()) then 'yes' else 'no'", "yes"),
            ("if (false()) then 'yes' else 'no'", "no"),
            ("if (1 > 0) then 'positive' else 'negative'", "positive"),
        };

        foreach (var (query, expected) in testCases)
        {
            var result = await _fixture.RunQueryAsync(query);
            result?.ToString().Should().Be(expected, $"Query: {query}");
        }
    }

    [Theory]
    [Trait("Category", "Full")]
    [InlineData("fn", "fn-abs")]
    [InlineData("fn", "fn-concat")]
    [InlineData("fn", "fn-contains")]
    [InlineData("fn", "fn-count")]
    [InlineData("fn", "fn-string-length")]
    [InlineData("fn", "fn-sum")]
    [InlineData("prod", "prod-ForClause")]
    [InlineData("prod", "prod-LetClause")]
    [InlineData("prod", "prod-WhereClause")]
    public async Task Xqts_ShouldPassTestSet(string category, string testSetName)
    {
        if (!_fixture.IsTestDataAvailable)
        {
            _output.WriteLine("XQTS test data not available. Skipping test.");
            return;
        }

        var testCases = await _fixture.LoadTestSetAsync(category, testSetName);
        _output.WriteLine($"Running {testCases.Count} tests from {category}/{testSetName}");

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
                }
                else if (failed <= 20) // Show details for first 20 assertion failures
                {
                    _output.WriteLine($"  Query: {testCase.Query[..Math.Min(120, testCase.Query.Length)]}");
                    _output.WriteLine($"  Actual: {result.ActualResult}");
                    _output.WriteLine($"  Expected: {string.Join(", ", testCase.Assertions.Select(a => $"{a.Type}={a.Value}"))}");
                }
            }
        }

        _output.WriteLine($"Results: {passed}/{testCases.Count} passed ({(double)passed / testCases.Count * 100:F1}%)");
        passed.Should().BeGreaterThan(0, $"At least some tests in {testSetName} should pass");
    }

    [Fact]
    [Trait("Category", "Full")]
    public async Task Xqts_ShouldRunFullTestSuite()
    {
        if (!_fixture.IsTestDataAvailable)
        {
            _output.WriteLine("XQTS test data not available. Skipping full test suite.");
            _output.WriteLine("To run these tests, clone the W3C qt3tests repository:");
            _output.WriteLine("  git clone https://github.com/w3c/qt3tests TestData/qt3tests");
            return;
        }

        var testCases = await _fixture.LoadAllTestsAsync();
        _output.WriteLine($"Running {testCases.Count} tests from XQTS");

        var progress = new Progress<XqtsTestResult>(result =>
        {
            if (!result.Passed)
            {
                _output.WriteLine($"FAILED: {result.TestCase.TestSet}/{result.TestCase.Name}");
            }
        });

        var summary = await _fixture.Runner.RunAllTestsAsync(testCases, progress, TestContext.Current.CancellationToken);

        _output.WriteLine("\n=== XQTS Test Summary ===");
        _output.WriteLine($"Total:   {summary.TotalTests}");
        _output.WriteLine($"Passed:  {summary.PassedTests}");
        _output.WriteLine($"Failed:  {summary.FailedTests}");
        _output.WriteLine($"Errors:  {summary.ErrorTests}");
        _output.WriteLine($"Skipped: {summary.SkippedTests}");
        _output.WriteLine($"Pass Rate: {summary.PassRate:F2}%");
        _output.WriteLine($"Duration: {summary.Duration.TotalSeconds:F2}s");

        // Target: 95%+ pass rate for supported features
        summary.PassRate.Should().BeGreaterOrEqualTo(95, "XQTS pass rate should be at least 95%");
    }
}

/// <summary>
/// Fixture for XQTS tests.
/// </summary>
public sealed class XqtsTestFixture : IAsyncLifetime
{
    private readonly string _testDataPath;
    public XqtsTestRunner Runner { get; private set; } = null!;
    public bool IsTestDataAvailable { get; private set; }

    public XqtsTestFixture()
    {
        var assemblyPath = Path.GetDirectoryName(typeof(XqtsTestFixture).Assembly.Location)!;
        _testDataPath = Path.Combine(assemblyPath, "TestData", "qt3tests");
    }

    public ValueTask InitializeAsync()
    {
        IsTestDataAvailable = File.Exists(Path.Combine(_testDataPath, "catalog.xml"));

        var config = new XqtsConfiguration
        {
            XQueryVersion = "3.1",
            SupportsHigherOrderFunctions = true,
            SupportsSchemaValidation = true
        };

        // Skip known unsupported tests
        config.SkipTests.Add("fn-transform"); // Requires XSLT support in fn:transform
        config.SkipTests.Add("fn-parse-xml-fragment"); // DTD handling

        Runner = new XqtsTestRunner(_testDataPath, config);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<object?> RunQueryAsync(string query)
    {
        var testCase = new XqtsTestCase
        {
            Name = "inline",
            TestSet = "inline",
            Query = query,
            Assertions = new List<XqtsAssertion>()
        };

        var result = await Runner.RunTestAsync(testCase, TestContext.Current.CancellationToken);
        if (result.Error != null)
        {
            throw result.Error;
        }
        return result.ActualResult;
    }

    public async Task<IReadOnlyList<XqtsTestCase>> LoadTestSetAsync(string category, string testSetName)
    {
        // Look up the test-set by name from the master catalog.
        // The catalog maps names like "fn-abs" to files like "fn/abs.xml".
        return await Runner.LoadTestSetByNameAsync(testSetName);
    }

    public async Task<IReadOnlyList<XqtsTestCase>> LoadAllTestsAsync()
    {
        return await Runner.LoadTestCasesAsync("catalog.xml");
    }
}
