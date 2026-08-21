using PhoenixmlDb.Core;
using System.Xml.Linq;
using PhoenixmlDb.XQuery;
using PhoenixmlDb.XQuery.Execution;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;

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

    /// <summary>
    /// Backs the context item. QT3 environments declare a source with <c>role="."</c>, which
    /// must reach the query as a DOCUMENT NODE; without a node provider the engine has no
    /// store to navigate and every path expression fails. Documents are cached by path
    /// because the corpus reuses a handful of sources across thousands of cases — fsx.xml
    /// alone backs most of prod/, and re-parsing it per test would dominate the run.
    /// </summary>
    private readonly XdmDocumentStore _documents = new();
    private readonly Dictionary<string, XdmDocument> _documentCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Serves <c>import schema</c>. The runner previously passed no schema provider at all, so
    /// any query importing a schema failed with "Cannot locate schema for namespace" no matter
    /// what the environment declared. Shared across tests and loaded on demand; the corpus has
    /// ~103 schema declarations over a handful of distinct .xsd files.
    /// </summary>
    private readonly XsdSchemaProvider _schemas = new();
    private readonly HashSet<string> _loadedSchemas = new(StringComparer.Ordinal);

    public XqtsTestRunner(string testDataPath, XqtsConfiguration? config = null)
    {
        _testDataPath = testDataPath;
        _config = config ?? new XqtsConfiguration();
        PreloadXmlNamespaceSchema(_schemas);
        _engine = new QueryEngine(
            nodeProvider: _documents, documentResolver: _documents, schemaProvider: _schemas);
    }

    /// <summary>
    /// Seeds the schema set with the XML-namespace attribute declarations (xml:lang, xml:space,
    /// xml:base, xml:id).
    /// </summary>
    /// <remarks>
    /// XSD processors are expected to know these implicitly; .NET's XmlSchemaSet does not. A
    /// corpus schema that merely REFERENCES xml:lang therefore fails to compile with
    /// "The 'http://www.w3.org/XML/1998/namespace:lang' attribute is not declared", and the
    /// import surfaces as the misleading "Cannot locate schema for namespace" — the schema is
    /// found, it just will not build. Verified against the suite's own loans.xsd.
    /// </remarks>
    private static void PreloadXmlNamespaceSchema(XsdSchemaProvider provider)
    {
        const string XmlNsXsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                       xmlns:xml="http://www.w3.org/XML/1998/namespace"
                       targetNamespace="http://www.w3.org/XML/1998/namespace">
              <xs:attribute name="lang"  type="xs:string"/>
              <xs:attribute name="space" type="xs:NCName"/>
              <xs:attribute name="base"  type="xs:anyURI"/>
              <xs:attribute name="id"    type="xs:ID"/>
            </xs:schema>
            """;
        try
        {
            provider.Add("http://www.w3.org/XML/1998/namespace", new StringReader(XmlNsXsd));
        }
        catch (PhoenixmlDb.XQuery.SchemaException)
        {
            // A provider that already knows the XML namespace needs no help.
        }
    }

    /// <summary>
    /// Registers documents the environment makes addressable by URI, so <c>fn:doc</c>,
    /// <c>fn:json-doc</c> and <c>fn:unparsed-text</c> can retrieve them. Without this the
    /// engine sees a bare relative name and reports "No document could be retrieved" or
    /// "Could not find a part of the path".
    /// </summary>
    private void RegisterUriDocuments(XqtsEnvironment? env)
    {
        if (env is null || env.UriDocuments.Count == 0) return;
        foreach (var (uri, path) in env.UriDocuments)
        {
            if (!_registeredUris.Add(uri)) continue;
            if (!File.Exists(path)) continue;
            // LoadFromString, not LoadFile: only the former accepts the document URI the test
            // will actually ask for. Non-XML resources (JSON, text) are registered by path
            // below instead — parsing them as XML would throw.
            try { _documents.LoadFromString(File.ReadAllText(path), uri); }
            catch (System.Xml.XmlException) { _nonXmlResources[uri] = path; }
        }
    }

    private readonly HashSet<string> _registeredUris = new(StringComparer.Ordinal);

    /// <summary>URIs whose backing file is not XML (JSON/text resources).</summary>
    private readonly Dictionary<string, string> _nonXmlResources = new(StringComparer.Ordinal);

    private int _eqStringFallbackRescues;

    /// <summary>
    /// How many assert-eq tests passed ONLY because the legacy string comparison rescued them
    /// after engine evaluation said unequal. Each one is a place where the harness may be
    /// masking a genuine engine inequality; a high count means the fallback is load-bearing
    /// and worth removing carefully rather than left in place.
    /// </summary>
    public int EqStringFallbackRescues => _eqStringFallbackRescues;

    /// <summary>
    /// Registers the environment's schemas so <c>import schema</c> can resolve them. Failures
    /// are swallowed: a schema this engine cannot compile must surface as the test's own error,
    /// not as a crash that takes the rest of the run with it.
    /// </summary>
    private void EnsureSchemasLoaded(XqtsEnvironment? env)
    {
        if (env is null || env.Schemas.Count == 0) return;
        foreach (var (uri, path) in env.Schemas)
        {
            if (!_loadedSchemas.Add(uri + "|" + path)) continue;
            if (!File.Exists(path)) continue;
            try { _schemas.ImportSchema(uri, [path]); }
            catch (PhoenixmlDb.XQuery.SchemaException) { }
        }
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

        // Environments resolve in two scopes. catalog.xml defines GLOBAL ones (works, staff,
        // atomic, empty, …) that any test-set may reference by name; a test-set may also
        // define its own, which take precedence. Only the test-set scope was loaded, so
        // `<environment ref="works"/>` found nothing and fell through to "parse inline",
        // producing an EMPTY environment — no context item, no variables, no namespaces.
        // That single omission accounts for the largest error clusters in the QT3 run:
        // "context item is absent" (815), "Variable $works is not defined" (~361),
        // "Unbound namespace prefix: atomic" (~234).
        var environments = new Dictionary<string, XqtsEnvironment>(LoadGlobalEnvironments());
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

    /// <summary>
    /// Environments declared at the top of <c>catalog.xml</c>, available to every test-set by
    /// name. Their source/schema paths resolve against the CATALOG's directory, not the
    /// referencing test-set's — which is why they cannot simply be re-parsed per test-set.
    /// Parsed once; the catalog is large and every test-set would otherwise re-read it.
    /// </summary>
    private Dictionary<string, XqtsEnvironment> LoadGlobalEnvironments()
    {
        if (_globalEnvironments is not null) return _globalEnvironments;

        var result = new Dictionary<string, XqtsEnvironment>(StringComparer.Ordinal);
        var catalogFile = Path.Combine(_testDataPath, "catalog.xml");
        if (File.Exists(catalogFile))
        {
            var catalog = XDocument.Load(catalogFile);
            var cns = catalog.Root?.Name.Namespace ?? XNamespace.None;
            var baseDir = Path.GetDirectoryName(catalogFile)!;
            // Only top-level <environment> children of the catalog root: a <test-set> element
            // inside the catalog is a REFERENCE to another file, not a definition.
            foreach (var envElem in catalog.Root?.Elements(cns + "environment") ?? [])
            {
                var name = envElem.Attribute("name")?.Value;
                if (name != null)
                    result[name] = ParseEnvironment(envElem, cns, baseDir);
            }
        }

        _globalEnvironments = result;
        return result;
    }

    private Dictionary<string, XqtsEnvironment>? _globalEnvironments;

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
                var full = Path.Combine(basePath, file);
                // role is "." (context item) or "$name" (bind the document to that variable).
                // Both were stored; only "." was ever consumed, so a query using $works got
                // "Variable $works is not defined" — 75 sources in the corpus use the $ form.
                env.Sources[role ?? "."] = full;

                // @uri makes the document addressable by fn:doc.
                var srcUri = source.Attribute("uri")?.Value;
                if (srcUri != null) env.UriDocuments[srcUri] = full;
            }
        }

        // <resource> declares a document addressable by URI — JSON for fn:json-doc, text for
        // fn:unparsed-text. Never parsed, so those tests reported "Could not find a part of the
        // path" against a relative name the engine had no way to resolve.
        foreach (var res in elem.Elements(ns + "resource"))
        {
            var file = res.Attribute("file")?.Value;
            var uri = res.Attribute("uri")?.Value;
            if (file != null && uri != null)
                env.UriDocuments[uri] = Path.Combine(basePath, file);
        }

        // Parse schemas. These were not parsed at all, so a query with `import schema` had
        // nothing to resolve against: "Cannot locate schema for namespace X" (~215 errors).
        foreach (var schema in elem.Elements(ns + "schema"))
        {
            var uri = schema.Attribute("uri")?.Value;
            var file = schema.Attribute("file")?.Value;
            if (uri != null && file != null)
                env.Schemas[uri] = Path.Combine(basePath, file);
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
            result.Passed = await VerifyAssertionsAsync(testCase.Assertions, queryResult, ct).ConfigureAwait(false);
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
        EnsureSchemasLoaded(testCase.Environment);
        RegisterUriDocuments(testCase.Environment);

        // Build query with environment parameter bindings
        var query = PrependEnvironmentBindings(testCase.Query, testCase.Environment);

        // Sources whose role is "$name" bind the DOCUMENT to that variable. Declaring them
        // external here (and supplying the value below) is what the "$" role means; without it
        // the query reported "Variable $works is not defined".
        var varSources = new List<(string Name, object? Doc)>();
        if (testCase.Environment is { } envForVars)
        {
            foreach (var (role, path) in envForVars.Sources)
            {
                if (role.Length < 2 || role[0] != '$' || !File.Exists(path)) continue;
                var name = role[1..];
                if (!_documentCache.TryGetValue(path, out var d))
                {
                    d = _documents.LoadFile(path);
                    _documentCache[path] = d;
                }
                varSources.Add((name, d));
                query = $"declare variable ${name} external;\n" + query;
            }
        }

        // Apply a per-test timeout on top of the caller's cancellation token
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerTestTimeout);
        var token = timeoutCts.Token;

        // Compile and execute query
        var results = new List<object?>();

        // NOTE: ExecuteAsync(string, ContainerId, object? initialContextItem, CancellationToken).
        // The token must go to the cancellationToken parameter — passing it positionally as the
        // 3rd arg made it the initialContextItem and left cancellation/timeout disabled (CA2016).
        //
        // Fixing that CA2016 warning by naming ONLY the token silently dropped the context item:
        // `contextItem` was still computed but never passed, so every path expression saw an
        // absent context and failed with "The context item is absent for '/'". Name both.
        var execCtx = _engine.CreateContext(initialContextItem: contextItem, cancellationToken: token);
        foreach (var (name, doc) in varSources)
            execCtx.SetExternalVariable(name, doc);

        var compiledQuery = _engine.Compile(query);
        if (!compiledQuery.Success || compiledQuery.ExecutionPlan is null)
            throw new XQueryRuntimeException("XPST0003",
                "Compilation failed: " + string.Join("; ", compiledQuery.Errors));

        await foreach (var item in compiledQuery.ExecutionPlan.ExecuteAsync(execCtx))
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
        if (env is null) return query;
        if (env.Parameters.Count == 0 && env.Namespaces.Count == 0) return query;

        var result = query;
        var prologue = new System.Text.StringBuilder();

        // Environment <namespace> declarations. These were parsed into env.Namespaces and then
        // never used — the dictionary had exactly one write and no reads — so a test whose
        // environment supplies the binding still failed with "Unbound namespace prefix: atomic".
        foreach (var (prefix, uri) in env.Namespaces)
        {
            if (string.IsNullOrEmpty(prefix))
                prologue.Append("declare default element namespace \"").Append(uri).Append("\";\n");
            else
                prologue.Append("declare namespace ").Append(prefix)
                        .Append(" = \"").Append(uri).Append("\";\n");
        }

        foreach (var (name, select) in env.Parameters)
        {
            // Preferred form: the query declares the variable external and we supply the value.
            var externalDecl = $"declare variable ${name} external";
            if (result.Contains(externalDecl, StringComparison.Ordinal))
            {
                result = result.Replace(externalDecl + ";", $"declare variable ${name} := {select};");
                result = result.Replace(externalDecl, $"declare variable ${name} := {select}");
                continue;
            }

            // Otherwise DECLARE it. Only the replace path existed, so an environment param used
            // by a query that never declared it — the common shape for the catalog's `works`
            // and `staff` environments — stayed unbound: "Variable $works is not defined".
            prologue.Append("declare variable $").Append(name).Append(" := ").Append(select).Append(";\n");
        }

        if (prologue.Length == 0) return result;

        // A version declaration must stay first in the module, so splice after it when present.
        var insertAt = FindPrologueInsertionPoint(result);
        return result[..insertAt] + prologue + result[insertAt..];
    }

    /// <summary>
    /// Returns the offset at which generated prolog declarations may be inserted: after a
    /// leading <c>xquery version "…";</c> if the module has one, otherwise the start.
    /// </summary>
    private static int FindPrologueInsertionPoint(string query)
    {
        var i = 0;
        while (i < query.Length && char.IsWhiteSpace(query[i])) i++;
        if (!query.AsSpan(i).StartsWith("xquery version", StringComparison.Ordinal)) return 0;
        var semi = query.IndexOf(';', i);
        return semi < 0 ? 0 : semi + 1;
    }

    /// <summary>
    /// Loads the environment's <c>role="."</c> source as the context item.
    /// </summary>
    /// <remarks>
    /// This used to <c>ReadAllText</c> and return the STRING — the comment said "parse as XML
    /// and return root node", but the parse was never written. The engine then received the
    /// raw markup as an xs:string context item and reported, accurately,
    /// <c>An axis step (Child::table) was used when the context item is not a node
    /// (got xs:string "&lt;tables&gt;&lt;table&gt;…")</c>. Every QT3 case needing a context
    /// document failed, and the engine was blamed for it.
    /// </remarks>
    private Task<object?> LoadContextItemAsync(XqtsEnvironment? env, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (env?.Sources.TryGetValue(".", out var sourcePath) != true || !File.Exists(sourcePath))
            return Task.FromResult<object?>(null);

        if (!_documentCache.TryGetValue(sourcePath!, out var doc))
        {
            doc = _documents.LoadFile(sourcePath!);
            _documentCache[sourcePath!] = doc;
        }
        return Task.FromResult<object?>(doc);
    }

    private async Task<bool> VerifyAssertionsAsync(
        List<XqtsAssertion> assertions, object? result, CancellationToken ct)
    {
        foreach (var assertion in assertions)
        {
            if (!await VerifyAssertionAsync(assertion, result, ct).ConfigureAwait(false))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Dispatches the assertion kinds that need to EVALUATE an expression (and therefore the
    /// engine, and therefore async); everything else goes to the synchronous switch.
    /// </summary>
    private async Task<bool> VerifyAssertionAsync(
        XqtsAssertion assertion, object? result, CancellationToken ct)
    {
        switch (assertion.Type)
        {
            case "assert":
                return await VerifyXPathAssertAsync(result, assertion.Value, ct).ConfigureAwait(false);
            case "assert-eq":
                // QT3 semantics: EVALUATE the expected expression and compare with `eq`.
                // VerifyEq compares STRINGS, with ad-hoc quote-stripping and constructor
                // special-casing — the same disease assert-deep-eq had, over 4050 corpus
                // occurrences.
                //
                // Engine first, legacy string compare as fallback. That ordering is
                // deliberately MONOTONIC: anything the string heuristic passed today still
                // passes, so this cannot regress. The cost is that a string-compare rescue can
                // MASK a real engine inequality, which is why the rescues are counted and
                // reported — a large number is a signal to investigate, not to celebrate.
                if (await VerifyXPathAssertAsync(
                        result, $"$result eq ({assertion.Value})", ct).ConfigureAwait(false))
                    return true;
                if (VerifyEq(result, assertion.Value))
                {
                    Interlocked.Increment(ref _eqStringFallbackRescues);
                    return true;
                }
                return false;
            case "assert-deep-eq":
                // QT3 semantics: EVALUATE the expected expression and compare with
                // fn:deep-equal. The old implementation was
                // `SerializeStringValue(result) == expected`, which is wrong twice over: it
                // compares a serialized result against expected SOURCE TEXT, and taking the
                // string value of a map raises FOTY0014 — "The string value of a map is not
                // defined" was 59 errors, all of them the harness stringifying a map result
                // for tests like parse-json("{}") deep-eq map{}.
                return await VerifyXPathAssertAsync(
                    result, $"deep-equal($result, ({assertion.Value}))", ct).ConfigureAwait(false);
            case "all-of":
                foreach (var c in assertion.Children)
                    if (!await VerifyAssertionAsync(c, result, ct).ConfigureAwait(false)) return false;
                return true;
            case "any-of":
                foreach (var c in assertion.Children)
                    if (await VerifyAssertionAsync(c, result, ct).ConfigureAwait(false)) return true;
                return false;
            default:
                return VerifyAssertion(assertion, result);
        }
    }

    /// <summary>
    /// Evaluates a plain <c>&lt;assert&gt;</c>: an XPath expression over the query result, with
    /// the result bound to <c>$result</c> (1383 of the corpus's ~1481 occurrences use it).
    /// </summary>
    /// <remarks>
    /// Previously there was no case for this at all, so it fell through the switch to
    /// <c>_ =&gt; false</c> and EVERY such test failed regardless of what the engine returned.
    /// A compile failure or a thrown expression is a failed assertion, not a harness crash —
    /// some assertions deliberately probe shapes the result may not have.
    /// </remarks>
    private async Task<bool> VerifyXPathAssertAsync(object? result, string? expr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expr)) return false;
        try
        {
            // $result must be DECLARED: an undeclared variable fails STATIC ANALYSIS, so a
            // runtime binding never gets a chance. The first version of this compiled the bare
            // expression, every compile failed, and the catch-all returned false — leaving the
            // method inert while looking implemented. It moved the QT3 pass rate by 2 tests.
            var compiled = _engine.Compile("declare variable $result external; " + expr);
            if (!compiled.Success || compiled.ExecutionPlan is null) return false;

            var ctx = _engine.CreateContext(cancellationToken: ct);
            // SetExternalVariable, not BindVariable: VariableDeclarationOperator resolves an
            // external declaration through TryGetExternalVariable, which BindVariable does not
            // populate ("External variable $result was not bound and has no default value").
            ctx.SetExternalVariable("result", AsXdmSequence(result));

            var items = new List<object?>();
            await foreach (var item in compiled.ExecutionPlan.ExecuteAsync(ctx).ConfigureAwait(false))
            {
                items.Add(item);
                if (items.Count > MaxResultCount) return false;
            }
            return EffectiveBooleanValue(items);
        }
        catch (XQueryRuntimeException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>
    /// Converts a runner-side result into the shape the engine treats as a SEQUENCE.
    /// </summary>
    /// <remarks>
    /// The engine's multi-item representation is <c>object?[]</c> — see e.g. fn:replicate,
    /// <c>seq is object?[] arr ? arr : new[] { seq }</c>. A <c>List&lt;object?&gt;</c> is
    /// therefore a SINGLE item: bound as-is, <c>count($result)</c> on a three-item list
    /// returned 1 and <c>$result[2]</c> returned nothing, which made every sequence-shaped
    /// assertion — most of the corpus — evaluate false.
    ///
    /// A string is deliberately NOT treated as a sequence despite being IEnumerable, and an
    /// XdmNode is a single item even where it exposes children.
    /// </remarks>
    private static object? AsXdmSequence(object? result) => result switch
    {
        null => Array.Empty<object?>(),
        object?[] => result,
        string => result,
        XdmNode => result,
        List<object?> list => list.ToArray(),
        IEnumerable<object?> seq => seq.ToArray(),
        _ => result
    };

    /// <summary>
    /// XPath effective boolean value, limited to what an assertion can yield: an empty
    /// sequence is false, a single boolean is itself, a single string/number is its own EBV,
    /// and any non-empty node sequence is true.
    /// </summary>
    private static bool EffectiveBooleanValue(List<object?> items)
    {
        if (items.Count == 0) return false;
        if (items.Count > 1) return true;
        return items[0] switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            double d => d != 0 && !double.IsNaN(d),
            decimal m => m != 0,
            int i => i != 0,
            long l => l != 0,
            _ => true
        };
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
            "assert-string-value" => SerializeStringValue(result) == assertion.Value,
            "assert-type" => VerifyType(result, assertion.Value),
            "assert-count" => VerifyCount(result, assertion.Value),
            "assert-xml" => VerifyXmlEqual(result, assertion.Value),
            "assert-permutation" => VerifyPermutation(result, assertion.Value),
            "error" => false, // Expected error, but we got a result
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

    /// <summary>Target namespace URI -> .xsd path, from the environment's &lt;schema&gt;.</summary>
    public Dictionary<string, string> Schemas { get; } = new();

    /// <summary>Document URI -> file path, for sources and resources addressable by fn:doc /
    /// fn:json-doc / fn:unparsed-text.</summary>
    public Dictionary<string, string> UriDocuments { get; } = new();
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
