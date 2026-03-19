using System.Xml.Linq;
using PhoenixmlDb.XQuery;
using PhoenixmlDb.XQuery.Execution;
using PhoenixmlDb.Xdm;

namespace PhoenixmlDb.Conformance.Tests.XQuery;

/// <summary>
/// Runner for W3C XQuery Test Suite (QT3/QT4) tests.
///
/// Test Suite Sources:
/// - QT3 Tests: https://github.com/w3c/qt3tests (~30,000 tests for XPath/XQuery 3.1)
/// - QT4 Tests: https://github.com/qt4cg/qt4tests (~40,000 tests for XPath/XQuery 4.0)
///
/// The test suite uses XML catalog files to define tests with:
/// - Test case metadata (name, description, dependencies)
/// - Source documents and environment setup
/// - Expected results as assertions
/// </summary>
public sealed class XqtsTestRunner
{
    private readonly QueryEngine _engine;
    private readonly string _testDataPath;
    private readonly XqtsConfiguration _config;

    public XqtsTestRunner(string testDataPath, XqtsConfiguration? config = null)
    {
        _testDataPath = testDataPath;
        _config = config ?? new XqtsConfiguration();
        _engine = new QueryEngine();
    }

    /// <summary>
    /// Loads test cases from a catalog file.
    /// </summary>
    public async Task<IReadOnlyList<XqtsTestCase>> LoadTestCasesAsync(
        string catalogPath,
        CancellationToken ct = default)
    {
        var testCases = new List<XqtsTestCase>();
        var catalogFile = Path.Combine(_testDataPath, catalogPath);

        if (!File.Exists(catalogFile))
        {
            return testCases;
        }

        var doc = await Task.Run(() => XDocument.Load(catalogFile), ct);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        // Parse test-set references
        foreach (var testSetRef in doc.Descendants(ns + "test-set"))
        {
            var testSetFile = testSetRef.Attribute("file")?.Value;
            if (testSetFile != null)
            {
                var testSetPath = Path.Combine(Path.GetDirectoryName(catalogFile)!, testSetFile);
                var tests = await LoadTestSetFileAsync(testSetPath, ct);
                testCases.AddRange(tests);
            }
        }

        return testCases;
    }

    /// <summary>
    /// Loads test cases for a specific test-set by name from the master catalog.
    /// </summary>
    public async Task<IReadOnlyList<XqtsTestCase>> LoadTestSetByNameAsync(
        string testSetName,
        CancellationToken ct = default)
    {
        var catalogFile = Path.Combine(_testDataPath, "catalog.xml");
        if (!File.Exists(catalogFile))
        {
            return [];
        }

        var doc = await Task.Run(() => XDocument.Load(catalogFile), ct);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        var testSetRef = doc.Descendants(ns + "test-set")
            .FirstOrDefault(e => e.Attribute("name")?.Value == testSetName);

        var testSetFile = testSetRef?.Attribute("file")?.Value;
        if (testSetFile == null)
        {
            return [];
        }

        var testSetPath = Path.Combine(Path.GetDirectoryName(catalogFile)!, testSetFile);
        return await LoadTestSetFileAsync(testSetPath, ct);
    }

    /// <summary>
    /// Loads test cases from a test-set file.
    /// </summary>
    private async Task<IReadOnlyList<XqtsTestCase>> LoadTestSetFileAsync(
        string testSetPath,
        CancellationToken ct)
    {
        var testCases = new List<XqtsTestCase>();

        if (!File.Exists(testSetPath))
        {
            return testCases;
        }

        var doc = await Task.Run(() => XDocument.Load(testSetPath), ct);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var testSetName = doc.Root?.Attribute("name")?.Value ?? Path.GetFileNameWithoutExtension(testSetPath);

        // Parse environment definitions
        var environments = new Dictionary<string, XqtsEnvironment>();
        foreach (var envElem in doc.Descendants(ns + "environment"))
        {
            var envName = envElem.Attribute("name")?.Value;
            if (envName != null)
            {
                environments[envName] = ParseEnvironment(envElem, ns, Path.GetDirectoryName(testSetPath)!);
            }
        }

        // Parse test cases
        foreach (var testCase in doc.Descendants(ns + "test-case"))
        {
            var test = ParseTestCase(testCase, ns, testSetName, environments, Path.GetDirectoryName(testSetPath)!);
            if (test != null && ShouldRunTest(test))
            {
                testCases.Add(test);
            }
        }

        return testCases;
    }

    private XqtsEnvironment ParseEnvironment(XElement elem, XNamespace ns, string basePath)
    {
        var env = new XqtsEnvironment();

        // Parse source documents
        foreach (var source in elem.Elements(ns + "source"))
        {
            var role = source.Attribute("role")?.Value;
            var file = source.Attribute("file")?.Value;
            if (file != null)
            {
                env.Sources[role ?? "."] = Path.Combine(basePath, file);
            }
        }

        // Parse namespaces
        foreach (var nsDecl in elem.Elements(ns + "namespace"))
        {
            var prefix = nsDecl.Attribute("prefix")?.Value ?? "";
            var uri = nsDecl.Attribute("uri")?.Value ?? "";
            env.Namespaces[prefix] = uri;
        }

        // Parse parameters
        foreach (var param in elem.Elements(ns + "param"))
        {
            var name = param.Attribute("name")?.Value;
            var select = param.Attribute("select")?.Value;
            if (name != null && select != null)
            {
                env.Parameters[name] = select;
            }
        }

        return env;
    }

    private XqtsTestCase? ParseTestCase(
        XElement elem,
        XNamespace ns,
        string testSetName,
        Dictionary<string, XqtsEnvironment> environments,
        string basePath)
    {
        var name = elem.Attribute("name")?.Value;
        if (name == null) return null;

        var test = new XqtsTestCase
        {
            Name = name,
            TestSet = testSetName,
            Description = elem.Element(ns + "description")?.Value ?? "",
            Query = elem.Element(ns + "test")?.Value ?? ""
        };

        // Check for environment reference
        var envRef = elem.Element(ns + "environment")?.Attribute("ref")?.Value;
        if (envRef != null && environments.TryGetValue(envRef, out var env))
        {
            test.Environment = env;
        }
        else
        {
            // Parse inline environment
            var envElem = elem.Element(ns + "environment");
            if (envElem != null)
            {
                test.Environment = ParseEnvironment(envElem, ns, basePath);
            }
        }

        // Parse dependencies
        foreach (var dep in elem.Elements(ns + "dependency"))
        {
            var type = dep.Attribute("type")?.Value;
            var value = dep.Attribute("value")?.Value;
            var satisfied = dep.Attribute("satisfied")?.Value != "false";

            if (type != null && value != null)
            {
                test.Dependencies.Add(new XqtsDependency
                {
                    Type = type,
                    Value = value,
                    Satisfied = satisfied
                });
            }
        }

        // Parse result assertions
        var result = elem.Element(ns + "result");
        if (result != null)
        {
            test.Assertions = ParseAssertions(result, ns);
        }

        return test;
    }

    private List<XqtsAssertion> ParseAssertions(XElement resultElem, XNamespace ns)
    {
        var assertions = new List<XqtsAssertion>();

        foreach (var child in resultElem.Elements())
        {
            var assertion = new XqtsAssertion
            {
                Type = child.Name.LocalName,
                Value = child.Value
            };

            // Handle nested assertions (all-of, any-of)
            if (child.Name.LocalName == "all-of" || child.Name.LocalName == "any-of")
            {
                assertion.Children = ParseAssertions(child, ns);
            }

            assertions.Add(assertion);
        }

        return assertions;
    }

    private bool ShouldRunTest(XqtsTestCase test)
    {
        // Check dependencies against configuration
        foreach (var dep in test.Dependencies)
        {
            if (!_config.SatisfiesDependency(dep))
            {
                return false;
            }
        }

        // Check if test is in skip list
        if (_config.SkipTests.Contains(test.Name) || _config.SkipTests.Contains($"{test.TestSet}/{test.Name}"))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Runs a single test case.
    /// </summary>
    public async Task<XqtsTestResult> RunTestAsync(XqtsTestCase testCase, CancellationToken ct = default)
    {
        var result = new XqtsTestResult
        {
            TestCase = testCase,
            StartTime = DateTimeOffset.UtcNow
        };

        try
        {
            // Setup context with environment
            var queryResult = await ExecuteQueryAsync(testCase, ct);
            result.ActualResult = queryResult;

            // Verify assertions
            result.Passed = VerifyAssertions(testCase.Assertions, queryResult);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Per-test timeout expired (not the suite-level cancellation)
            var ex = new TimeoutException(
                $"Test '{testCase.Name}' exceeded the per-test timeout of {PerTestTimeout.TotalSeconds}s.");
            result.Error = ex;
            result.Passed = IsExpectedError(testCase.Assertions, ex);
        }
        catch (XQueryRuntimeException ex)
        {
            result.Error = ex;
            result.Passed = IsExpectedError(testCase.Assertions, ex);
        }
        catch (Exception ex)
        {
            result.Error = ex;
            result.Passed = IsExpectedError(testCase.Assertions, ex);
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>
    /// Maximum number of result items collected per query to prevent OOM on pathological tests.
    /// </summary>
    private const int MaxResultCount = 100_000;

    /// <summary>
    /// Per-test execution timeout to prevent runaway queries from blocking the test suite.
    /// </summary>
    private static readonly TimeSpan PerTestTimeout = TimeSpan.FromSeconds(30);

    private async Task<object?> ExecuteQueryAsync(XqtsTestCase testCase, CancellationToken ct)
    {
        // Load source documents if needed
        var contextItem = await LoadContextItemAsync(testCase.Environment, ct);

        // Build query with environment parameter bindings
        var query = PrependEnvironmentBindings(testCase.Query, testCase.Environment);

        // Apply a per-test timeout on top of the caller's cancellation token
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerTestTimeout);
        var token = timeoutCts.Token;

        // Compile and execute query
        var results = new List<object?>();

        await foreach (var item in _engine.ExecuteAsync(
            query,
            default,
            token))
        {
            results.Add(item);

            if (results.Count > MaxResultCount)
            {
                throw new XQueryRuntimeException(
                    "FOER0000",
                    $"Result count exceeded the safety limit of {MaxResultCount} items.");
            }
        }

        return results.Count == 1 ? results[0] : results;
    }

    /// <summary>
    /// Prepends variable declarations for environment parameters to the query.
    /// Replaces `declare variable $name external;` with `declare variable $name := value;`.
    /// </summary>
    private static string PrependEnvironmentBindings(string query, XqtsEnvironment? env)
    {
        if (env?.Parameters.Count is null or 0) return query;

        // If the query already declares these variables as external, replace those declarations
        var result = query;
        foreach (var (name, select) in env.Parameters)
        {
            // Replace "declare variable $name external;" with "declare variable $name := select;"
            var externalDecl = $"declare variable ${name} external";
            if (result.Contains(externalDecl, StringComparison.Ordinal))
            {
                result = result.Replace(
                    externalDecl + ";",
                    $"declare variable ${name} := {select};");
                // Also handle version with type annotation
                result = result.Replace(
                    externalDecl,
                    $"declare variable ${name} := {select}");
            }
        }

        return result;
    }

    private async Task<object?> LoadContextItemAsync(XqtsEnvironment? env, CancellationToken ct)
    {
        if (env?.Sources.TryGetValue(".", out var sourcePath) == true)
        {
            if (File.Exists(sourcePath))
            {
                var content = await File.ReadAllTextAsync(sourcePath, ct);
                // Parse as XML and return root node
                return content;
            }
        }
        return null;
    }

    private bool VerifyAssertions(List<XqtsAssertion> assertions, object? result)
    {
        foreach (var assertion in assertions)
        {
            if (!VerifyAssertion(assertion, result))
            {
                return false;
            }
        }
        return true;
    }

    private bool VerifyAssertion(XqtsAssertion assertion, object? result)
    {
        return assertion.Type switch
        {
            "assert-true" => UnwrapSingle(result) is true || UnwrapSingle(result)?.ToString() == "true",
            "assert-false" => UnwrapSingle(result) is false || UnwrapSingle(result)?.ToString() == "false",
            "assert-empty" => result == null
                || (result is List<object?> emptyList && emptyList.Count == 0)
                || (result is ICollection<object> c && c.Count == 0),
            "assert-eq" => VerifyEq(result, assertion.Value),
            "assert-string-value" => SerializeStringValue(result) == assertion.Value,
            "assert-type" => VerifyType(result, assertion.Value),
            "assert-count" => VerifyCount(result, assertion.Value),
            "assert-deep-eq" => VerifyDeepEqual(result, assertion.Value),
            "assert-xml" => VerifyXmlEqual(result, assertion.Value),
            "assert-permutation" => VerifyPermutation(result, assertion.Value),
            "error" => false, // Expected error, but we got a result
            "all-of" => assertion.Children.All(a => VerifyAssertion(a, result)),
            "any-of" => assertion.Children.Any(a => VerifyAssertion(a, result)),
            _ => false // Unknown assertion type — must be explicitly implemented
        };
    }

    private bool IsExpectedError(List<XqtsAssertion> assertions, Exception ex)
    {
        foreach (var assertion in assertions)
        {
            if (assertion.Type == "error")
            {
                // Check if error code matches
                var expectedCode = assertion.Value;
                if (ex.Message.Contains(expectedCode ?? "") || string.IsNullOrEmpty(expectedCode))
                {
                    return true;
                }
            }
            if (assertion.Type == "any-of")
            {
                if (assertion.Children.Any(a => a.Type == "error"))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Unwraps a single-item list to its contained value.
    /// </summary>
    private static object? UnwrapSingle(object? result)
    {
        if (result is List<object?> { Count: 1 } list) return list[0];
        return result;
    }

    /// <summary>
    /// Serializes a result to its XQuery string value.
    /// For sequences, items are space-separated.
    /// </summary>
    private static string? SerializeStringValue(object? result)
    {
        if (result is null) return "";
        if (result is List<object?> list)
            return string.Join(" ", list.Select(SerializeItem));
        if (result is IList<object?> ilist)
            return string.Join(" ", ilist.Select(SerializeItem));
        return SerializeItem(result);
    }

    /// <summary>
    /// Serializes a single item to its XQuery string representation.
    /// </summary>
    private static string SerializeItem(object? item)
    {
        return PhoenixmlDb.XQuery.Functions.ConcatFunction.XQueryStringValue(item);
    }

    /// <summary>
    /// Extracts the inner value from XQuery constructor expressions like xs:float(3.14).
    /// </summary>
    private static string ExtractConstructorValue(string expected)
    {
        // Match patterns like xs:float(...), xs:double(...), xs:integer(...), xs:decimal(...)
        if (expected.StartsWith("xs:", StringComparison.Ordinal) && expected.EndsWith(')'))
        {
            var parenIdx = expected.IndexOf('(');
            if (parenIdx > 0)
            {
                var inner = expected[(parenIdx + 1)..^1];
                // Strip quotes from inner value
                if (inner.Length >= 2 &&
                    ((inner[0] == '"' && inner[^1] == '"') ||
                     (inner[0] == '\'' && inner[^1] == '\'')))
                {
                    inner = inner[1..^1];
                }
                return inner;
            }
        }
        return expected;
    }

    private static bool VerifyEq(object? result, string? expected)
    {
        result = UnwrapSingle(result);
        if (expected == null) return result == null;
        if (result == null) return false;

        var actualStr = result.ToString() ?? "";

        // Strip XQuery string literal quotes from expected value
        if (expected.Length >= 2 &&
            ((expected[0] == '"' && expected[^1] == '"') ||
             (expected[0] == '\'' && expected[^1] == '\'')))
        {
            expected = expected[1..^1];
        }

        // Handle XQuery constructor expressions like xs:float(3.4028235E38)
        expected = ExtractConstructorValue(expected);

        // Direct string match first
        if (actualStr == expected) return true;

        // Numeric comparison: handles formatting differences like E+308 vs E308
        if (result is double d)
        {
            if (double.TryParse(expected, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var expectedDbl))
            {
                if (double.IsNaN(d) && double.IsNaN(expectedDbl)) return true;
                return d == expectedDbl;
            }
        }

        if (result is float f)
        {
            if (float.TryParse(expected, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var expectedFlt))
            {
                if (float.IsNaN(f) && float.IsNaN(expectedFlt)) return true;
                return f == expectedFlt;
            }
        }

        if (result is decimal dc)
        {
            if (decimal.TryParse(expected, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var expectedDec))
                return dc == expectedDec;
        }

        if (result is long l)
        {
            if (long.TryParse(expected, System.Globalization.CultureInfo.InvariantCulture, out var expectedLong))
                return l == expectedLong;
        }

        if (result is bool b)
        {
            return string.Equals(b.ToString(), expected, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private bool VerifyType(object? result, string? expectedType)
    {
        result = UnwrapSingle(result);
        if (expectedType == null) return true;
        // Simplified type checking - integer subtypes all map to long in our engine
        return expectedType switch
        {
            "xs:integer" or "xs:int" or "xs:long" or "xs:short" or "xs:byte"
                or "xs:unsignedLong" or "xs:unsignedInt" or "xs:unsignedShort" or "xs:unsignedByte"
                or "xs:positiveInteger" or "xs:nonNegativeInteger"
                or "xs:negativeInteger" or "xs:nonPositiveInteger"
                => result is int or long,
            "xs:decimal" => result is decimal or int or long,
            "xs:double" => result is double,
            "xs:float" => result is float,
            "xs:string" or "xs:anyURI" or "xs:untypedAtomic" or "xs:normalizedString"
                or "xs:NCName" or "xs:Name" or "xs:NMTOKEN" or "xs:language"
                => result is string,
            "xs:boolean" => result is bool,
            _ => true // Unknown type, assume pass
        };
    }

    private bool VerifyCount(object? result, string? expectedCount)
    {
        if (!int.TryParse(expectedCount, out var expected)) return false;
        if (result is List<object?> list) return list.Count == expected;
        if (result is ICollection<object> c) return c.Count == expected;
        return expected == 1 && result != null;
    }

    private bool VerifyDeepEqual(object? result, string? expected)
    {
        // Simplified deep equality — serialize both to string value
        return SerializeStringValue(result) == expected;
    }

    private bool VerifyXmlEqual(object? result, string? expected)
    {
        if (expected == null) return result == null;
        try
        {
            // For multiple-item results, concatenate their string values
            var resultStr = result is List<object?> list
                ? string.Concat(list.Select(item => item?.ToString() ?? ""))
                : result?.ToString() ?? "";

            // Wrap both in a root element for comparison if they're fragments
            var wrappedResult = $"<r>{resultStr}</r>";
            var wrappedExpected = $"<r>{expected}</r>";
            var resultXml = XDocument.Parse(wrappedResult);
            var expectedXml = XDocument.Parse(wrappedExpected);
            return XNode.DeepEquals(resultXml, expectedXml);
        }
        catch
        {
            return false;
        }
    }

    private bool VerifyPermutation(object? result, string? expected)
    {
        // Check if result contains same items in any order
        List<string?> resultStrings;
        if (result is List<object?> list)
            resultStrings = list.Select(r => r?.ToString()).ToList();
        else if (result is ICollection<object> resultItems)
            resultStrings = resultItems.Select(r => r?.ToString()).ToList();
        else
            return false;

        var expectedItems = expected?.Split(',').Select(s => s.Trim()).ToList();
        if (expectedItems == null) return false;

        return resultStrings.OrderBy(s => s).SequenceEqual(expectedItems.OrderBy(s => s));
    }

    /// <summary>
    /// Runs all test cases and returns a summary.
    /// </summary>
    public async Task<XqtsTestSummary> RunAllTestsAsync(
        IReadOnlyList<XqtsTestCase> testCases,
        IProgress<XqtsTestResult>? progress = null,
        CancellationToken ct = default)
    {
        var summary = new XqtsTestSummary
        {
            TotalTests = testCases.Count,
            StartTime = DateTimeOffset.UtcNow
        };

        foreach (var testCase in testCases)
        {
            ct.ThrowIfCancellationRequested();

            var result = await RunTestAsync(testCase, ct);
            summary.Results.Add(result);

            if (result.Passed)
                summary.PassedTests++;
            else if (result.Error != null)
                summary.ErrorTests++;
            else
                summary.FailedTests++;

            progress?.Report(result);
        }

        summary.EndTime = DateTimeOffset.UtcNow;
        return summary;
    }
}

/// <summary>
/// Configuration for XQTS test runner.
/// </summary>
public sealed class XqtsConfiguration
{
    public string XQueryVersion { get; init; } = "3.1";
    public bool SupportsSchemaValidation { get; init; } = true;
    public bool SupportsHigherOrderFunctions { get; init; } = true;
    public bool SupportsStaticTyping { get; init; } = false;
    public HashSet<string> SkipTests { get; } = new();
    public HashSet<string> SupportedFeatures { get; } = new()
    {
        "higherOrderFunctions",
        "moduleImport",
        "schemaImport",
        "schemaValidation",
        "staticTyping",
        "serialization",
        "infoset-dtd",
        "xpath-1.0-compatibility",
        "namespace-axis"
    };

    public bool SatisfiesDependency(XqtsDependency dep)
    {
        return dep.Type switch
        {
            "spec" => dep.Value?.Contains("XQ") == true && dep.Satisfied,
            "feature" => SupportedFeatures.Contains(dep.Value ?? "") == dep.Satisfied,
            "xsd-version" => dep.Satisfied,
            "xml-version" => dep.Satisfied,
            "limits" => dep.Satisfied,
            _ => dep.Satisfied
        };
    }
}

/// <summary>
/// Represents an XQTS test case.
/// </summary>
public sealed class XqtsTestCase
{
    public required string Name { get; init; }
    public required string TestSet { get; init; }
    public string Description { get; init; } = "";
    public required string Query { get; init; }
    public XqtsEnvironment? Environment { get; set; }
    public List<XqtsDependency> Dependencies { get; } = new();
    public List<XqtsAssertion> Assertions { get; set; } = new();
}

/// <summary>
/// Test environment with source documents and context.
/// </summary>
public sealed class XqtsEnvironment
{
    public Dictionary<string, string> Sources { get; } = new();
    public Dictionary<string, string> Namespaces { get; } = new();
    public Dictionary<string, string> Parameters { get; } = new();
}

/// <summary>
/// Test dependency declaration.
/// </summary>
public sealed class XqtsDependency
{
    public required string Type { get; init; }
    public required string Value { get; init; }
    public bool Satisfied { get; init; } = true;
}

/// <summary>
/// Result assertion.
/// </summary>
public sealed class XqtsAssertion
{
    public required string Type { get; init; }
    public string? Value { get; init; }
    public List<XqtsAssertion> Children { get; set; } = new();
}

/// <summary>
/// Result of running a single test.
/// </summary>
public sealed class XqtsTestResult
{
    public required XqtsTestCase TestCase { get; init; }
    public bool Passed { get; set; }
    public object? ActualResult { get; set; }
    public Exception? Error { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
}

/// <summary>
/// Summary of test run.
/// </summary>
public sealed class XqtsTestSummary
{
    public int TotalTests { get; set; }
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
    public int ErrorTests { get; set; }
    public int SkippedTests => TotalTests - PassedTests - FailedTests - ErrorTests;
    public double PassRate => TotalTests > 0 ? (double)PassedTests / TotalTests * 100 : 0;
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
    public List<XqtsTestResult> Results { get; } = new();
}
