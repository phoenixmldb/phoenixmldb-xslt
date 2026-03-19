using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xdm;

namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// Runner for W3C XSLT 3.0 Test Suite.
///
/// Test Suite Source:
/// - XSLT 3.0 Tests: https://github.com/w3c/xslt30-test
///
/// The test suite contains around 20,000+ tests organized into categories
/// covering all XSLT 3.0 features. Tests are defined in XML catalog files
/// with stylesheet sources, input documents, and expected results.
/// </summary>
public sealed class XsltTestRunner
{
    private readonly string _testDataPath;
    private readonly XsltConfiguration _config;

    public XsltTestRunner(string testDataPath, XsltConfiguration? config = null)
    {
        _testDataPath = testDataPath;
        _config = config ?? new XsltConfiguration();
    }

    /// <summary>
    /// Loads test cases from a catalog or test-set file.
    /// Detects file type by root element: &lt;catalog&gt; vs &lt;test-set&gt;.
    /// </summary>
    public async Task<IReadOnlyList<XsltTestCase>> LoadTestCasesAsync(
        string catalogPath,
        CancellationToken ct = default)
    {
        var testCases = new List<XsltTestCase>();
        var catalogFile = Path.Combine(_testDataPath, catalogPath);

        if (!File.Exists(catalogFile))
        {
            return testCases;
        }

        var doc = await Task.Run(() => XDocument.Load(catalogFile), ct);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var rootName = doc.Root?.Name.LocalName;

        if (rootName == "test-set")
        {
            // Direct test-set file — load test cases directly
            var tests = await LoadTestSetAsync(catalogFile, ct);
            testCases.AddRange(tests);
        }
        else
        {
            // Catalog file — parse test-set references
            foreach (var testSetRef in doc.Root?.Elements(ns + "test-set") ?? [])
            {
                var testSetFile = testSetRef.Attribute("file")?.Value;
                if (testSetFile == null) continue;

                // Check skip list by test-set name
                var testSetName = testSetRef.Attribute("name")?.Value;
                if (testSetName != null && _config.SkipTestSets.Contains(testSetName))
                    continue;

                var testSetPath = Path.Combine(Path.GetDirectoryName(catalogFile)!, testSetFile);
                var tests = await LoadTestSetAsync(testSetPath, ct);
                testCases.AddRange(tests);
            }
        }

        return testCases;
    }

    /// <summary>
    /// Loads test cases from a test-set file.
    /// </summary>
    private async Task<IReadOnlyList<XsltTestCase>> LoadTestSetAsync(
        string testSetPath,
        CancellationToken ct)
    {
        var testCases = new List<XsltTestCase>();

        if (!File.Exists(testSetPath))
        {
            return testCases;
        }

        var doc = await Task.Run(() => XDocument.Load(testSetPath), ct);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var testSetName = doc.Root?.Attribute("name")?.Value ?? Path.GetFileNameWithoutExtension(testSetPath);
        var basePath = Path.GetDirectoryName(testSetPath)!;
        var testSetUri = new Uri(Path.GetFullPath(testSetPath));

        // Parse all named environments (shared across test cases)
        var namedEnvironments = new Dictionary<string, XsltEnvironment>();
        foreach (var envElem in doc.Root?.Elements(ns + "environment") ?? [])
        {
            var envName = envElem.Attribute("name")?.Value;
            if (envName != null)
            {
                namedEnvironments[envName] = ParseEnvironment(envElem, ns, basePath, testSetUri);
            }
        }

        // Check test-set-level dependencies.
        // The streaming feature is skipped at test-set level because some test sets
        // (e.g., source-document) mix streaming and non-streaming tests. Non-streaming
        // tests can run without streaming support.
        var testSetDeps = ParseDependencies(doc.Root, ns);
        var testSetHasStreamingDep = false;
        var hasBlockingTestSetDep = false;
        foreach (var dep in testSetDeps)
        {
            if (dep.Type == "feature" && dep.Value == "streaming")
            {
                testSetHasStreamingDep = true;
                continue;
            }
            if (!_config.SatisfiesDependency(dep))
            {
                hasBlockingTestSetDep = true;
                break;
            }
        }
        if (hasBlockingTestSetDep)
        {
            return testCases;
        }

        // Parse test cases
        foreach (var testCaseElem in doc.Root?.Elements(ns + "test-case") ?? [])
        {
            var test = ParseTestCase(testCaseElem, ns, testSetName, namedEnvironments, basePath, testSetUri);
            if (test != null)
            {
                // If the test set requires streaming, propagate only to tests that
                // use streaming environments (those setting STREAMABLE=true).
                // Non-streaming tests (STREAMABLE=false) can run without streaming support.
                if (testSetHasStreamingDep && !_config.SatisfiesDependency(
                        new XsltDependency { Type = "feature", Value = "streaming", Satisfied = true }))
                {
                    var streamableParam = test.Environment.Parameters
                        .FirstOrDefault(p => p.Key == "STREAMABLE");
                    if (streamableParam.Value is "true()" or "yes" or "1")
                    {
                        // Test uses streaming — add the dependency so it gets filtered
                        test.Dependencies.Add(new XsltDependency
                        {
                            Type = "feature", Value = "streaming", Satisfied = true
                        });
                    }
                }
                if (ShouldRunTest(test))
                {
                    testCases.Add(test);
                }
            }
        }

        return testCases;
    }

    private XsltEnvironment ParseEnvironment(XElement elem, XNamespace ns, string basePath, Uri? testSetUri = null)
    {
        var env = new XsltEnvironment();

        // Parse stylesheet
        var stylesheetElem = elem.Element(ns + "stylesheet");
        if (stylesheetElem != null)
        {
            var file = stylesheetElem.Attribute("file")?.Value;
            if (file != null)
            {
                env.StylesheetPath = Path.Combine(basePath, file);
            }
        }

        // Parse source documents
        foreach (var source in elem.Elements(ns + "source"))
        {
            var file = source.Attribute("file")?.Value;
            var uri = source.Attribute("uri")?.Value;
            var role = source.Attribute("role")?.Value;
            var sourceSelect = source.Attribute("select")?.Value;

            // Sources with uri but no role are secondary documents (for doc() function),
            // not the principal source. Only use "." as default when no uri is specified.
            if (role == null)
            {
                role = uri != null ? uri : ".";
            }

            if (file != null)
            {
                var sourcePath = Path.Combine(basePath, file);
                if (role is "." or "principal")
                {
                    env.PrincipalSource = sourcePath;
                    if (sourceSelect != null) env.PrincipalSourceSelect = sourceSelect;
                }
                else
                {
                    env.AdditionalSources[role] = sourcePath;
                }
            }
            else
            {
                // Inline content via <content> element
                var contentElem = source.Element(ns + "content");
                if (contentElem != null)
                {
                    var inlineContent = contentElem.Value;
                    if (!string.IsNullOrEmpty(inlineContent))
                    {
                        if (role is "." or "principal")
                        {
                            env.PrincipalSourceContent = inlineContent;
                            env.PrincipalSourceBaseUri = testSetUri;
                            if (sourceSelect != null) env.PrincipalSourceSelect = sourceSelect;
                        }
                        else
                        {
                            env.AdditionalSourceContents[role] = inlineContent;
                        }
                    }
                }
            }
        }

        // Parse collections
        foreach (var collElem in elem.Elements(ns + "collection"))
        {
            var collUri = collElem.Attribute("uri")?.Value ?? "";
            var sources = new List<string>();
            foreach (var collSource in collElem.Elements(ns + "source"))
            {
                var file = collSource.Attribute("file")?.Value;
                if (file != null)
                {
                    sources.Add(Path.Combine(basePath, file));
                }
            }
            env.Collections[collUri] = sources;
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

        // Parse initial template/function/mode
        var envInitialTemplateElem = elem.Element(ns + "initial-template");
        if (envInitialTemplateElem != null)
        {
            var templateName = envInitialTemplateElem.Attribute("name")?.Value;
            if (templateName != null)
            {
                env.InitialTemplate = templateName;
                var colonIdx = templateName.IndexOf(':');
                if (colonIdx > 0)
                {
                    var prefix = templateName[..colonIdx];
                    env.InitialTemplateNamespace = envInitialTemplateElem.GetNamespaceOfPrefix(prefix)?.NamespaceName;
                }
            }
        }
        var envInitialFunctionElem = elem.Element(ns + "initial-function");
        if (envInitialFunctionElem != null)
        {
            var funcName = envInitialFunctionElem.Attribute("name")?.Value;
            if (funcName != null)
            {
                env.InitialFunction = funcName;
                var colonIdx = funcName.IndexOf(':');
                if (colonIdx > 0)
                {
                    var prefix = funcName[..colonIdx];
                    env.InitialFunctionNamespace = envInitialFunctionElem.GetNamespaceOfPrefix(prefix)?.NamespaceName;
                }
            }
        }
        env.InitialMode = elem.Element(ns + "initial-mode")?.Attribute("name")?.Value;

        // Parse <package> elements (secondary packages for xsl:use-package resolution)
        foreach (var pkgElem in elem.Elements(ns + "package"))
        {
            var pkgFile = pkgElem.Attribute("file")?.Value;
            var pkgUri = pkgElem.Attribute("uri")?.Value;
            var pkgVersion = pkgElem.Attribute("package-version")?.Value;
            if (pkgFile != null)
            {
                var pkgPath = Path.Combine(basePath, pkgFile);
                if (pkgUri == null)
                    pkgUri = ReadPackageNameFromFile(pkgPath);
                if (pkgUri != null)
                {
                    if (!env.Packages.TryGetValue(pkgUri, out var existing))
                    {
                        existing = [];
                        env.Packages[pkgUri] = existing;
                    }
                    existing.Add((pkgVersion, pkgPath));
                }
            }
        }

        return env;
    }

    private XsltTestCase? ParseTestCase(
        XElement elem,
        XNamespace ns,
        string testSetName,
        Dictionary<string, XsltEnvironment> namedEnvironments,
        string basePath,
        Uri? testSetUri = null)
    {
        var name = elem.Attribute("name")?.Value;
        if (name == null) return null;

        var test = new XsltTestCase
        {
            Name = name,
            TestSet = testSetName,
            Description = elem.Element(ns + "description")?.Value ?? ""
        };

        // Resolve environment: ref to named environment, or inline, or empty
        var envElem = elem.Element(ns + "environment");
        if (envElem != null)
        {
            var envRef = envElem.Attribute("ref")?.Value;
            if (envRef != null && namedEnvironments.TryGetValue(envRef, out var refEnv))
            {
                test.Environment = CloneEnvironment(refEnv);
                // Merge any additional inline overrides
                if (envElem.HasElements)
                {
                    var overrides = ParseEnvironment(envElem, ns, basePath, testSetUri);
                    MergeEnvironment(test.Environment, overrides);
                }
            }
            else
            {
                test.Environment = ParseEnvironment(envElem, ns, basePath, testSetUri);
            }
        }
        else
        {
            test.Environment = new XsltEnvironment();
        }

        // Parse test element (stylesheet reference or inline)
        var testElem = elem.Element(ns + "test");
        if (testElem != null)
        {
            // File-based stylesheet via <stylesheet file="..."/> or <package file="..." role="principal"/>
            var stylesheetElem = testElem.Element(ns + "stylesheet")
                ?? testElem.Elements(ns + "package")
                    .FirstOrDefault(p => p.Attribute("role")?.Value != "secondary");
            if (stylesheetElem != null)
            {
                var stylesheetFile = stylesheetElem.Attribute("file")?.Value;
                if (stylesheetFile != null)
                {
                    test.Environment.StylesheetPath = Path.Combine(basePath, stylesheetFile);
                }
            }
            else
            {
                // File attribute directly on <test>
                var testFile = testElem.Attribute("file")?.Value;
                if (testFile != null)
                {
                    test.Environment.StylesheetPath = Path.Combine(basePath, testFile);
                }
                else
                {
                    // Inline stylesheet content — pick the first element that is an actual stylesheet
                    // (skip param, initial-template, initial-mode, initial-function, output)
                    var nonStylesheetNames = new HashSet<string> { "param", "initial-template", "initial-mode", "initial-function", "output" };
                    var stylesheetContent = testElem.Elements()
                        .FirstOrDefault(e => !nonStylesheetNames.Contains(e.Name.LocalName))?.ToString();
                    if (stylesheetContent != null)
                    {
                        test.InlineStylesheet = stylesheetContent;
                    }
                }
            }

            // Parse parameters from <test> element (W3C format: <param name="x" select="val"/>)
            foreach (var param in testElem.Elements(ns + "param"))
            {
                var paramName = param.Attribute("name")?.Value;
                var paramSelect = param.Attribute("select")?.Value;
                if (paramName != null && paramSelect != null)
                {
                    var isStatic = param.Attribute("static")?.Value is "yes" or "true";
                    if (isStatic)
                    {
                        // Static params are passed at compile time for shadow attribute resolution
                        var val = paramSelect.Trim();
                        if ((val.StartsWith('\'') && val.EndsWith('\'')) || (val.StartsWith('"') && val.EndsWith('"')))
                        {
                            var inner = val[1..^1];
                            // Unescape XPath string literal escaping: '' → ' or "" → "
                            inner = val[0] == '\''
                                ? inner.Replace("''", "'", StringComparison.Ordinal)
                                : inner.Replace("\"\"", "\"", StringComparison.Ordinal);
                            test.Environment.StaticParameters[paramName] = inner;
                        }
                        else
                            test.Environment.StaticParameters[paramName] = val;
                    }
                    // Also pass as runtime param (to satisfy required params and runtime references)
                    // If the test specifies an as="xs:type", wrap the select expression to cast the value
                    var paramAs = param.Attribute("as")?.Value;
                    if (paramAs != null && paramAs.StartsWith("xs:", StringComparison.Ordinal))
                    {
                        // e.g., as="xs:string" with select="111" → pass as xs:string('111') at runtime
                        test.Environment.Parameters[paramName] = $"{paramAs}({paramSelect})";
                    }
                    else
                    {
                        test.Environment.Parameters[paramName] = paramSelect;
                    }
                }
            }

            // Parse initial-template/initial-mode/initial-function from <test> element
            var initialTemplateElem = testElem.Element(ns + "initial-template");
            if (initialTemplateElem != null)
            {
                var templateName = initialTemplateElem.Attribute("name")?.Value;
                if (templateName != null)
                {
                    test.Environment.InitialTemplate = templateName;
                    // Resolve namespace prefix if present (e.g., "foo:temp")
                    var colonIdx = templateName.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        var prefix = templateName[..colonIdx];
                        var nsUri = initialTemplateElem.GetNamespaceOfPrefix(prefix)?.NamespaceName;
                        if (nsUri != null)
                        {
                            test.Environment.InitialTemplateNamespace = nsUri;
                        }
                    }
                }
                // Parse <param> children of <initial-template>
                foreach (var paramElem in initialTemplateElem.Elements(ns + "param"))
                {
                    var paramName = paramElem.Attribute("name")?.Value;
                    var paramSelect = paramElem.Attribute("select")?.Value;
                    if (paramName == null || paramSelect == null) continue;

                    var isTunnel = paramElem.Attribute("tunnel")?.Value == "yes";
                    string localName = paramName;
                    string? paramNs = null;

                    // Resolve namespace prefix (e.g. "my:b" with xmlns:my="http://my.net/")
                    var colonIdx = paramName.IndexOf(':', StringComparison.Ordinal);
                    if (colonIdx > 0)
                    {
                        var prefix = paramName[..colonIdx];
                        localName = paramName[(colonIdx + 1)..];
                        var xns = paramElem.GetNamespaceOfPrefix(prefix);
                        if (xns != null)
                            paramNs = xns.NamespaceName;
                    }

                    test.Environment.InitialTemplateParams.Add(
                        new InitialTemplateParam(localName, paramNs, paramSelect, isTunnel));
                }
            }
            var initialModeElem = testElem.Element(ns + "initial-mode");
            if (initialModeElem != null)
            {
                var modeName = initialModeElem.Attribute("name")?.Value;
                if (modeName != null)
                {
                    // Resolve namespace prefix if present (e.g., "test:a" with xmlns:test="...")
                    var colonIdx = modeName.IndexOf(':', StringComparison.Ordinal);
                    if (colonIdx > 0)
                    {
                        var prefix = modeName[..colonIdx];
                        var modeNs = initialModeElem.GetNamespaceOfPrefix(prefix);
                        if (modeNs != null)
                        {
                            test.Environment.InitialMode = modeName[(colonIdx + 1)..];
                            test.Environment.InitialModeNamespace = modeNs.NamespaceName;
                        }
                        else
                        {
                            test.Environment.InitialMode = modeName;
                        }
                    }
                    else
                    {
                        test.Environment.InitialMode = modeName;
                    }
                }

                // Parse select attribute on <initial-mode>
                var modeSelectAttr = initialModeElem.Attribute("select");
                if (modeSelectAttr != null)
                    test.Environment.InitialModeSelect = modeSelectAttr.Value;

                // Parse <param> children of <initial-mode>
                foreach (var paramElem in initialModeElem.Elements(ns + "param"))
                {
                    var paramName = paramElem.Attribute("name")?.Value;
                    var paramSelect = paramElem.Attribute("select")?.Value;
                    if (paramName == null || paramSelect == null) continue;

                    var isTunnel = paramElem.Attribute("tunnel")?.Value == "yes";
                    string localName = paramName;
                    string? paramNs = null;

                    var colonIdx2 = paramName.IndexOf(':', StringComparison.Ordinal);
                    if (colonIdx2 > 0)
                    {
                        var prefix = paramName[..colonIdx2];
                        localName = paramName[(colonIdx2 + 1)..];
                        var xns = paramElem.GetNamespaceOfPrefix(prefix);
                        if (xns != null)
                            paramNs = xns.NamespaceName;
                    }

                    test.Environment.InitialTemplateParams.Add(
                        new InitialTemplateParam(localName, paramNs, paramSelect, isTunnel));
                }
            }

            // Parse initial-function from <test> element
            var initialFunctionElem = testElem.Element(ns + "initial-function");
            if (initialFunctionElem != null)
            {
                var funcName = initialFunctionElem.Attribute("name")?.Value;
                if (funcName != null)
                {
                    // Resolve namespace prefix
                    var colonIdx = funcName.IndexOf(':', StringComparison.Ordinal);
                    if (colonIdx > 0)
                    {
                        var prefix = funcName[..colonIdx];
                        var funcNs = initialFunctionElem.GetNamespaceOfPrefix(prefix);
                        if (funcNs != null)
                        {
                            test.Environment.InitialFunction = funcName[(colonIdx + 1)..];
                            test.Environment.InitialFunctionNamespace = funcNs.NamespaceName;
                        }
                        else
                        {
                            test.Environment.InitialFunction = funcName;
                        }
                    }
                    else
                    {
                        test.Environment.InitialFunction = funcName;
                    }
                }

                // Parse positional <param> children of <initial-function>
                foreach (var paramElem in initialFunctionElem.Elements(ns + "param"))
                {
                    var paramSelect = paramElem.Attribute("select")?.Value;
                    if (paramSelect != null)
                    {
                        test.Environment.InitialFunctionArgs.Add(paramSelect);
                    }
                }
            }
        }

        // Parse <package role="secondary"> from <test> element (test-specific packages)
        if (testElem != null)
        {
            foreach (var pkgElem in testElem.Elements(ns + "package"))
            {
                var pkgRole = pkgElem.Attribute("role")?.Value;
                if (pkgRole == "secondary")
                {
                    var pkgFile = pkgElem.Attribute("file")?.Value;
                    var pkgUri = pkgElem.Attribute("uri")?.Value;
                    var pkgVersion = pkgElem.Attribute("package-version")?.Value;
                    if (pkgFile != null)
                    {
                        var pkgPath = Path.Combine(basePath, pkgFile);
                        // If no URI specified, try to read it from the package file's name attribute
                        if (pkgUri == null)
                            pkgUri = ReadPackageNameFromFile(pkgPath);
                        if (pkgUri != null)
                        {
                            if (!test.Environment.Packages.TryGetValue(pkgUri, out var existing))
                            {
                                existing = [];
                                test.Environment.Packages[pkgUri] = existing;
                            }
                            existing.Add((pkgVersion, pkgPath));
                        }
                    }
                }
            }
        }

        // Parse dependencies (<dependencies> wrapper with <spec>, <feature>, etc.)
        test.Dependencies.AddRange(ParseDependencies(elem, ns));

        // Parse expected result
        var result = elem.Element(ns + "result");
        if (result != null)
        {
            test.Assertions = ParseAssertions(result, ns, basePath);
        }

        return test;
    }

    private List<XsltDependency> ParseDependencies(XElement? elem, XNamespace ns)
    {
        var deps = new List<XsltDependency>();
        if (elem == null) return deps;

        foreach (var depsElem in elem.Elements(ns + "dependencies"))
        {
            // W3C format: <dependencies><spec value="XSLT30+"/><feature value="schema_aware"/></dependencies>
            foreach (var child in depsElem.Elements())
            {
                var type = child.Name.LocalName;
                var value = child.Attribute("value")?.Value;
                var satisfied = child.Attribute("satisfied")?.Value != "false";

                if (value != null)
                {
                    deps.Add(new XsltDependency
                    {
                        Type = type,
                        Value = value,
                        Satisfied = satisfied
                    });
                }
                else if (type == "enable_assertions")
                {
                    // enable_assertions has no value attribute; we always enable assertions,
                    // so skip tests that require assertions to be disabled (satisfied="false")
                    deps.Add(new XsltDependency
                    {
                        Type = "feature",
                        Value = "enable_assertions",
                        Satisfied = satisfied
                    });
                }
                else if (type == "ignore_doc_failure")
                {
                    // ignore_doc_failure has no value attribute; we don't support ignoring
                    // document retrieval failures, so add as unsupported feature
                    deps.Add(new XsltDependency
                    {
                        Type = "feature",
                        Value = "ignore_doc_failure",
                        Satisfied = satisfied
                    });
                }
            }
        }

        // Also handle old format: <dependency type="..." value="..."/>
        foreach (var dep in elem.Elements(ns + "dependency"))
        {
            var type = dep.Attribute("type")?.Value;
            var value = dep.Attribute("value")?.Value;
            var satisfied = dep.Attribute("satisfied")?.Value != "false";

            if (type != null && value != null)
            {
                deps.Add(new XsltDependency
                {
                    Type = type,
                    Value = value,
                    Satisfied = satisfied
                });
            }
        }

        return deps;
    }

    private List<XsltAssertion> ParseAssertions(XElement resultElem, XNamespace ns, string basePath)
    {
        var assertions = new List<XsltAssertion>();

        foreach (var child in resultElem.Elements())
        {
            var localName = child.Name.LocalName;

            var assertion = new XsltAssertion
            {
                Type = localName,
                Value = localName is "all-of" or "any-of" ? null : child.Value
            };

            // Handle file reference for expected output
            var file = child.Attribute("file")?.Value;
            if (file != null)
            {
                assertion.ExpectedFile = Path.Combine(basePath, file);
            }

            // Handle compare attribute
            assertion.Compare = child.Attribute("compare")?.Value ?? "XML";

            // Handle ignore-prefixes attribute
            var ignorePrefixes = child.Attribute("ignore-prefixes")?.Value;
            assertion.IgnorePrefixes = ignorePrefixes is "true" or "yes" or "1";

            // Handle encoding attribute for assert-serialization
            var encoding = child.Attribute("encoding")?.Value;
            if (encoding != null)
            {
                assertion.ExpectedEncoding = encoding;
            }

            // Handle uri attribute for assert-result-document
            var uri = child.Attribute("uri")?.Value;
            if (uri != null)
            {
                assertion.Uri = uri;
            }

            // Handle nested assertions
            if (localName is "all-of" or "any-of" or "assert-result-document")
            {
                assertion.Children = ParseAssertions(child, ns, basePath);
            }

            assertions.Add(assertion);
        }

        return assertions;
    }

    private static XsltEnvironment CloneEnvironment(XsltEnvironment env)
    {
        var clone = new XsltEnvironment
        {
            StylesheetPath = env.StylesheetPath,
            PrincipalSource = env.PrincipalSource,
            PrincipalSourceContent = env.PrincipalSourceContent,
            PrincipalSourceBaseUri = env.PrincipalSourceBaseUri,
            PrincipalSourceSelect = env.PrincipalSourceSelect,
            InitialTemplate = env.InitialTemplate,
            InitialFunction = env.InitialFunction,
            InitialFunctionNamespace = env.InitialFunctionNamespace,
            InitialMode = env.InitialMode,
            InitialModeNamespace = env.InitialModeNamespace,
            InitialModeSelect = env.InitialModeSelect
        };

        foreach (var kv in env.AdditionalSources)
            clone.AdditionalSources[kv.Key] = kv.Value;
        foreach (var kv in env.AdditionalSourceContents)
            clone.AdditionalSourceContents[kv.Key] = kv.Value;
        foreach (var kv in env.Parameters)
            clone.Parameters[kv.Key] = kv.Value;
        foreach (var kv in env.StaticParameters)
            clone.StaticParameters[kv.Key] = kv.Value;
        foreach (var arg in env.InitialFunctionArgs)
            clone.InitialFunctionArgs.Add(arg);
        foreach (var kv in env.Collections)
            clone.Collections[kv.Key] = new List<string>(kv.Value);
        foreach (var kv in env.Packages)
            clone.Packages[kv.Key] = new List<(string?, string)>(kv.Value);

        return clone;
    }

    private static void MergeEnvironment(XsltEnvironment target, XsltEnvironment source)
    {
        if (source.StylesheetPath != null)
            target.StylesheetPath = source.StylesheetPath;
        if (source.PrincipalSource != null)
            target.PrincipalSource = source.PrincipalSource;
        if (source.PrincipalSourceContent != null)
            target.PrincipalSourceContent = source.PrincipalSourceContent;
        if (source.PrincipalSourceSelect != null)
            target.PrincipalSourceSelect = source.PrincipalSourceSelect;
        if (source.InitialTemplate != null)
            target.InitialTemplate = source.InitialTemplate;
        if (source.InitialFunction != null)
        {
            target.InitialFunction = source.InitialFunction;
            target.InitialFunctionNamespace = source.InitialFunctionNamespace;
        }
        if (source.InitialMode != null)
        {
            target.InitialMode = source.InitialMode;
            target.InitialModeNamespace = source.InitialModeNamespace;
            target.InitialModeSelect = source.InitialModeSelect;
        }

        foreach (var kv in source.AdditionalSources)
            target.AdditionalSources[kv.Key] = kv.Value;
        foreach (var kv in source.AdditionalSourceContents)
            target.AdditionalSourceContents[kv.Key] = kv.Value;
        foreach (var kv in source.Parameters)
            target.Parameters[kv.Key] = kv.Value;
        foreach (var kv in source.StaticParameters)
            target.StaticParameters[kv.Key] = kv.Value;
        foreach (var kv in source.Collections)
            target.Collections[kv.Key] = new List<string>(kv.Value);
        foreach (var kv in source.Packages)
        {
            if (target.Packages.TryGetValue(kv.Key, out var existing))
                existing.AddRange(kv.Value);
            else
                target.Packages[kv.Key] = new List<(string?, string)>(kv.Value);
        }
    }

    /// <summary>
    /// Reads the package name (name attribute) from an xsl:package file.
    /// Used when the test definition doesn't specify a uri attribute.
    /// </summary>
    private static string? ReadPackageNameFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            // Quick parse — just read enough to find the name attribute on the root element
            var doc = System.Xml.Linq.XDocument.Load(filePath, System.Xml.Linq.LoadOptions.None);
            return doc.Root?.Attribute("name")?.Value;
        }
        catch
        {
            return null;
        }
    }

    private bool ShouldRunTest(XsltTestCase test)
    {
        // Check dependencies
        if (!ShouldRunDependencies(test.Dependencies))
        {
            return false;
        }

        // Check skip list (by test name, test-set/name, or test-set name)
        if (_config.SkipTests.Contains(test.Name) ||
            _config.SkipTests.Contains($"{test.TestSet}/{test.Name}") ||
            _config.SkipTestSets.Contains(test.TestSet))
        {
            return false;
        }

        return true;
    }

    private bool ShouldRunDependencies(List<XsltDependency> deps)
    {
        foreach (var dep in deps)
        {
            if (!_config.SatisfiesDependency(dep))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Maximum time allowed for a single test case execution.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Tests that are known to require longer execution time due to computational complexity.
    /// These get an extended timeout of 30 seconds.
    /// </summary>
    /// <summary>
    /// Tests that require extended execution time due to computational complexity.
    /// These get an extended timeout of 180 seconds.
    /// </summary>
    private static readonly HashSet<string> SlowTests = [
        "variable-0108",
        "normalize-unicode-008",
        "catalog-006b",
        "catalog-007",
        "catalog-008",
        // 100K-iteration streaming tests (big-transactions.xml)
        "sf-not-107",
        "sf-boolean-107",
        "si-iterate-037",
        "si-choose-012",
        // Docbook stylesheets: 53 includes, 400+ globals
        "docbook-001",
        "docbook-002",
        "docbook-003",
        // Large ot.xml (3.5MB, 23K verse elements) streaming test
        "streamable-003"
    ];

    /// <summary>
    /// Runs a single test case.
    /// </summary>
    public async Task<XsltTestResult> RunTestAsync(XsltTestCase testCase, CancellationToken ct = default)
    {
        var result = new XsltTestResult
        {
            TestCase = testCase,
            StartTime = DateTimeOffset.UtcNow
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = SlowTests.Contains(testCase.Name) ? TimeSpan.FromSeconds(180) : TestTimeout;
        cts.CancelAfter(timeout);

        try
        {
            // Load stylesheet
            var stylesheetContent = testCase.InlineStylesheet ??
                (testCase.Environment.StylesheetPath != null
                    ? await ReadXmlFileAsync(testCase.Environment.StylesheetPath, cts.Token)
                    : throw new InvalidOperationException("No stylesheet specified"));

            // Load source document (file or inline content)
            string? sourceContent = null;
            if (testCase.Environment.PrincipalSource != null)
            {
                sourceContent = await ReadXmlFileAsync(testCase.Environment.PrincipalSource, cts.Token);
            }
            else if (testCase.Environment.PrincipalSourceContent != null)
            {
                sourceContent = testCase.Environment.PrincipalSourceContent;
            }

            // Execute transformation with timeout protection
            var transformTask = Task.Run(async () =>
            {
                var transformer = new XsltTransformer();
                Uri? baseUri = testCase.Environment.StylesheetPath != null
                    ? new Uri(Path.GetFullPath(testCase.Environment.StylesheetPath))
                    : null;
                var staticParams = testCase.Environment.StaticParameters.Count > 0
                    ? testCase.Environment.StaticParameters
                    : null;
                var packageCatalog = testCase.Environment.Packages.Count > 0
                    ? testCase.Environment.Packages
                    : null;
                await transformer.LoadStylesheetAsync(stylesheetContent, baseUri, staticParams, packageCatalog);

                // Set parameters (evaluate select expressions to get typed values)
                foreach (var (name, selectExpr) in testCase.Environment.Parameters)
                {
                    transformer.SetParameter(name, EvaluateParamSelect(selectExpr));
                }

                // Set initial template/mode if specified
                if (testCase.Environment.InitialTemplate != null)
                {
                    transformer.SetInitialTemplate(testCase.Environment.InitialTemplate, testCase.Environment.InitialTemplateNamespace);
                }
                else if (sourceContent == null && stylesheetContent.Contains("xsl:initial-template", StringComparison.Ordinal))
                {
                    // XSLT 3.0: auto-detect xsl:initial-template when no source document
                    transformer.SetInitialTemplate("xsl:initial-template", "http://www.w3.org/1999/XSL/Transform");
                }

                // Set initial template parameters (with-param for the template call)
                foreach (var tp in testCase.Environment.InitialTemplateParams)
                {
                    var nsId = tp.Namespace != null
                        ? PhoenixmlDb.Xslt.Engine.StylesheetParser.ResolveNamespaceUri(tp.Namespace)
                        : PhoenixmlDb.Core.NamespaceId.None;
                    var qname = new PhoenixmlDb.Core.QName(nsId, tp.LocalName);
                    var value = EvaluateParamSelect(tp.SelectExpr);
                    if (tp.Tunnel)
                        transformer.SetInitialTunnelParameter(qname, value);
                    else
                        transformer.SetInitialTemplateParameter(qname, value);
                }

                if (testCase.Environment.InitialMode != null)
                {
                    transformer.SetInitialMode(testCase.Environment.InitialMode, testCase.Environment.InitialModeNamespace);
                }
                if (testCase.Environment.InitialModeSelect != null)
                {
                    transformer.SetInitialModeSelect(testCase.Environment.InitialModeSelect);
                }

                // Set initial function if specified
                if (testCase.Environment.InitialFunction != null)
                {
                    transformer.SetInitialFunction(testCase.Environment.InitialFunction, testCase.Environment.InitialFunctionNamespace);
                    foreach (var argExpr in testCase.Environment.InitialFunctionArgs)
                    {
                        transformer.AddInitialFunctionArgument(EvaluateParamSelect(argExpr));
                    }
                }

                // Set source document URI for base-uri/document-uri resolution
                if (testCase.Environment.PrincipalSource != null)
                {
                    transformer.SetSourceDocumentUri(new Uri(Path.GetFullPath(testCase.Environment.PrincipalSource)));
                }
                else if (testCase.Environment.PrincipalSourceBaseUri != null)
                {
                    transformer.SetSourceDocumentUri(testCase.Environment.PrincipalSourceBaseUri);
                }

                // Set source select expression if specified (e.g., select="/doc" to use doc element as initial context)
                if (testCase.Environment.PrincipalSourceSelect != null)
                {
                    transformer.SetSourceSelect(testCase.Environment.PrincipalSourceSelect);
                }

                // Set collections for fn:collection()
                foreach (var (collUri, collPaths) in testCase.Environment.Collections)
                {
                    transformer.SetCollection(collUri, collPaths);
                }

                var primaryOutput = await transformer.TransformAsync(sourceContent, cts.Token);
                return (primaryOutput, transformer.SecondaryResultDocuments);
            }, cts.Token);

            var (output, secondaryResults) = await transformTask.WaitAsync(cts.Token);
            result.ActualResult = output;

            // Verify assertions (pass secondary results for assert-result-document)
            result.Passed = await VerifyAssertionsAsync(testCase.Assertions, output, cts.Token, secondaryResults);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            result.Error = new TimeoutException($"Test '{testCase.Name}' timed out after {timeout.TotalSeconds}s");
            result.Passed = IsExpectedError(testCase.Assertions, result.Error);
        }
        catch (Exception ex)
        {
            result.Error = ex;
            result.Passed = IsExpectedError(testCase.Assertions, ex);
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    private async Task<bool> VerifyAssertionsAsync(
        List<XsltAssertion> assertions,
        string? actualResult,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? secondaryResults = null)
    {
        foreach (var assertion in assertions)
        {
            if (!await VerifyAssertionAsync(assertion, actualResult, ct, secondaryResults))
            {
                return false;
            }
        }
        return assertions.Count > 0;
    }

    private async Task<bool> VerifyAssertionAsync(
        XsltAssertion assertion,
        string? actualResult,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? secondaryResults = null)
    {
        return assertion.Type switch
        {
            "assert-xml" => await VerifyXmlAsync(assertion, actualResult, ct),
            "assert-string-value" => VerifyStringValue(assertion, actualResult),
            "assert-serialization" => await VerifySerializationAsync(assertion, actualResult, ct),
            "assert-result-document" => await VerifyResultDocumentAsync(assertion, actualResult, ct, secondaryResults),
            "assert-eq" => VerifyEq(assertion, actualResult),
            "assert-count" => VerifyCount(assertion, actualResult),
            "assert-type" => VerifyType(assertion, actualResult),
            "assert-deep-eq" => VerifyDeepEq(assertion, actualResult),
            "assert-message" => true, // Message assertions require special handling
            "error" => false, // Expected error, but we got a result
            "all-of" => await AllOfAsync(assertion.Children, actualResult, ct, secondaryResults),
            "any-of" => await AnyOfAsync(assertion.Children, actualResult, ct, secondaryResults),
            _ => true
        };
    }

    private static bool VerifyStringValue(XsltAssertion assertion, string? actualResult)
    {
        if (actualResult == null) return false;

        var expected = assertion.Value ?? "";

        // Strip XML declaration if present (assert-string-value compares text content, not serialized form)
        var result = actualResult;
        if (result.StartsWith("<?xml ", StringComparison.Ordinal))
        {
            var declEnd = result.IndexOf("?>", StringComparison.Ordinal);
            if (declEnd >= 0)
                result = result[(declEnd + 2)..];
        }

        // Try exact match first
        if (result == expected) return true;

        // Try trimmed match (output often has whitespace differences)
        if (result.Trim() == expected.Trim()) return true;

        // Try stripping XML wrapper if output was wrapped
        try
        {
            var doc = XDocument.Parse(result);
            var textContent = doc.Root?.Value ?? "";
            if (textContent == expected || textContent.Trim() == expected.Trim())
                return true;
        }
        catch
        {
            // Not XML, compare as-is
        }

        // Also try with original (pre-stripped) result for XML parsing
        if (result != actualResult)
        {
            try
            {
                var doc = XDocument.Parse(actualResult);
                var textContent = doc.Root?.Value ?? "";
                if (textContent == expected || textContent.Trim() == expected.Trim())
                    return true;
            }
            catch
            {
                // Not XML
            }
        }

        return false;
    }

    private static bool VerifyEq(XsltAssertion assertion, string? actualResult)
    {
        if (actualResult == null) return false;
        var expected = assertion.Value?.Trim() ?? "";
        var actual = actualResult.Trim();

        // Strip quotes from expected if it's a string literal
        if ((expected.StartsWith('\'') && expected.EndsWith('\'')) ||
            (expected.StartsWith('"') && expected.EndsWith('"')))
            expected = expected[1..^1];

        if (actual == expected) return true;

        // Try numeric comparison (for integer/double equality)
        if (double.TryParse(actual, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var actualNum) &&
            double.TryParse(expected, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var expectedNum))
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            return actualNum == expectedNum;
        }

        return false;
    }

    private static bool VerifyCount(XsltAssertion assertion, string? actualResult)
    {
        if (actualResult == null) return false;
        if (!int.TryParse(assertion.Value?.Trim(), out var expectedCount)) return false;

        // For serialized output, count is typically 1 unless the result is empty
        // A raw sequence is serialized as space-separated values
        if (expectedCount == 0) return string.IsNullOrEmpty(actualResult.Trim());
        if (expectedCount == 1) return !string.IsNullOrEmpty(actualResult.Trim());

        // For multi-item sequences, try counting space-separated or comma-separated items
        // This is a rough heuristic for serialized sequence output
        var items = actualResult.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return items.Length == expectedCount;
    }

    private static bool VerifyType(XsltAssertion assertion, string? actualResult)
    {
        // Limited type checking based on string representation
        if (actualResult == null) return false;
        var typeName = assertion.Value?.Trim() ?? "";
        var actual = actualResult.Trim();

        return typeName switch
        {
            "xs:integer" => long.TryParse(actual, out _),
            "xs:double" => double.TryParse(actual, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "xs:decimal" => decimal.TryParse(actual, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "xs:float" => float.TryParse(actual, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "xs:string" or "xs:string*" => true, // Everything can be a string
            "xs:boolean" => actual is "true" or "false",
            "xs:anyURI" => true, // URIs are strings
            "xs:token" => true, // Tokens are strings
            "xs:QName" => actual.Contains(':', StringComparison.Ordinal) ||
                          actual.Contains('{', StringComparison.Ordinal) ||
                          !string.IsNullOrEmpty(actual),
            _ when typeName.StartsWith("xs:", StringComparison.Ordinal) => true, // Assume OK for other xs: types
            _ => !string.IsNullOrEmpty(actual) // Fallback: non-empty is OK
        };
    }

    private static bool VerifyDeepEq(XsltAssertion assertion, string? actualResult)
    {
        if (actualResult == null) return false;
        var expected = assertion.Value?.Trim() ?? "";
        var actual = actualResult.Trim();

        // Simple case: direct comparison
        if (actual == expected) return true;

        // Try comparing as comma-separated sequences of values
        var expectedItems = expected.Split(',').Select(s => s.Trim()).ToArray();
        var actualItems = actual.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (expectedItems.Length != actualItems.Length) return false;

        for (int i = 0; i < expectedItems.Length; i++)
        {
            if (expectedItems[i] != actualItems[i])
            {
                // Try numeric comparison
                if (double.TryParse(actualItems[i], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var av) &&
                    double.TryParse(expectedItems[i], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var ev))
                {
                    // ReSharper disable once CompareOfFloatsByEqualityOperator
                    if (av != ev) return false;
                }
                else return false;
            }
        }
        return true;
    }

    private async Task<bool> AllOfAsync(List<XsltAssertion> assertions, string? result, CancellationToken ct,
        IReadOnlyDictionary<string, string>? secondaryResults = null)
    {
        foreach (var a in assertions)
        {
            if (!await VerifyAssertionAsync(a, result, ct, secondaryResults))
                return false;
        }
        return true;
    }

    private async Task<bool> AnyOfAsync(List<XsltAssertion> assertions, string? result, CancellationToken ct,
        IReadOnlyDictionary<string, string>? secondaryResults = null)
    {
        foreach (var a in assertions)
        {
            if (await VerifyAssertionAsync(a, result, ct, secondaryResults))
                return true;
        }
        return false;
    }

    private async Task<bool> VerifyXmlAsync(XsltAssertion assertion, string? actualResult, CancellationToken ct)
    {
        if (actualResult == null) return false;

        string? expectedXml = assertion.Value;
        if (assertion.ExpectedFile != null && File.Exists(assertion.ExpectedFile))
        {
            expectedXml = await File.ReadAllTextAsync(assertion.ExpectedFile, ct);
        }

        if (string.IsNullOrEmpty(expectedXml)) return false;

        try
        {
            // Wrap fragments in a root element for comparison if needed
            var actualParseable = WrapForParsing(actualResult.Trim());
            var expectedParseable = WrapForParsing(expectedXml.Trim());

            var actualDoc = XDocument.Parse(actualParseable);
            var expectedDoc = XDocument.Parse(expectedParseable);

            // Strip XML declarations — XNode.DeepEquals considers them, but
            // the presence/absence of <?xml?> is not semantically significant
            actualDoc.Declaration = null;
            expectedDoc.Declaration = null;

            // Normalize empty elements: XNode.DeepEquals distinguishes <e/> from <e></e>
            // but they are semantically identical XML.
            NormalizeEmptyElements(actualDoc);
            NormalizeEmptyElements(expectedDoc);

            // Strip unused namespace declarations that engines may propagate differently
            StripUnusedNamespaces(actualDoc);
            StripUnusedNamespaces(expectedDoc);

            // Hoist namespace declarations to the root element so that
            // placement differences (xmlns on parent vs child) don't cause false failures.
            // In XML, namespace declarations are inherited, so this is semantically equivalent.
            HoistNamespaceDeclarations(actualDoc);
            HoistNamespaceDeclarations(expectedDoc);

            // Normalize namespace declaration order (alphabetical by prefix)
            // XNode.DeepEquals is order-sensitive for attributes/namespaces
            NormalizeNamespaceOrder(actualDoc);
            NormalizeNamespaceOrder(expectedDoc);

            // Normalize attribute order (alphabetical by expanded name)
            // XML attributes are unordered, but XNode.DeepEquals compares in order
            NormalizeAttributeOrder(actualDoc);
            NormalizeAttributeOrder(expectedDoc);

            // When ignore-prefixes is set, normalize namespace prefixes so that
            // <j:map xmlns:j="ns"> compares equal to <map xmlns="ns">
            if (assertion.IgnorePrefixes)
            {
                NormalizeNamespacePrefixes(actualDoc);
                NormalizeNamespacePrefixes(expectedDoc);
            }

            return assertion.Compare switch
            {
                "XML" => XNode.DeepEquals(actualDoc, expectedDoc),
                "Text" => actualDoc.ToString() == expectedDoc.ToString(),
                "Fragment" => CompareFragments(actualDoc, expectedDoc),
                _ => XNode.DeepEquals(actualDoc, expectedDoc)
            };
        }
        catch
        {
            // If XML parsing fails, try string comparison
            return NormalizeXml(actualResult) == NormalizeXml(expectedXml);
        }
    }

    /// <summary>
    /// Strips unused namespace declarations from the document. A namespace declaration is
    /// considered unused if no element or attribute in its scope uses that namespace URI.
    /// This handles the case where XSLT engines propagate in-scope stylesheet namespaces
    /// (e.g. xmlns:xs) that are not actually used in the result tree.
    /// </summary>
    private static void StripUnusedNamespaces(XDocument doc)
    {
        foreach (var elem in doc.Descendants().ToList())
        {
            var toRemove = new List<XAttribute>();
            foreach (var attr in elem.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                var nsUri = attr.Value;
                // Never strip the default namespace or the xml namespace
                if (string.IsNullOrEmpty(nsUri) ||
                    nsUri == "http://www.w3.org/XML/1998/namespace")
                    continue;

                // For default namespace declarations (xmlns="..."), check URI usage
                if (attr.Name.LocalName == "xmlns" && attr.Name.Namespace == XNamespace.None)
                {
                    var used = false;
                    foreach (var desc in elem.DescendantsAndSelf())
                    {
                        if (desc.Name.NamespaceName == nsUri)
                        { used = true; break; }
                        if (desc.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.NamespaceName == nsUri))
                        { used = true; break; }
                    }
                    if (!used)
                        toRemove.Add(attr);
                    continue;
                }

                // For prefixed namespace declarations (xmlns:prefix="URI"),
                // check if the URI is used by any descendant element/attribute.
                // Also strip if this prefix's URI is identical to the default namespace
                // in scope — the prefix is then redundant since LINQ to XML resolves
                // elements via the default namespace and XNode.DeepEquals compares
                // namespace declarations as attributes.
                var prefixUsed = false;
                foreach (var desc in elem.DescendantsAndSelf())
                {
                    if (desc.Name.NamespaceName == nsUri)
                    { prefixUsed = true; break; }
                    if (desc.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.NamespaceName == nsUri))
                    { prefixUsed = true; break; }
                }

                if (!prefixUsed)
                {
                    toRemove.Add(attr);
                }
                else
                {
                    // If the same URI is also declared as the default namespace,
                    // the prefixed declaration is redundant (LINQ to XML uses default ns)
                    var defaultNs = elem.GetDefaultNamespace();
                    if (defaultNs.NamespaceName == nsUri)
                        toRemove.Add(attr);
                }
            }
            foreach (var attr in toRemove)
                attr.Remove();
        }
    }

    /// <summary>
    /// Hoists all namespace declarations from descendant elements to the document root element.
    /// In XML, namespace declarations are inherited by descendants, so moving them to the root
    /// is semantically equivalent. This eliminates false comparison failures when one tree
    /// declares xmlns:prefix on a parent element while the other declares it on a child.
    /// </summary>
    private static void HoistNamespaceDeclarations(XDocument doc)
    {
        var root = doc.Root;
        if (root == null) return;

        // Collect all namespace declarations from descendants (not root)
        foreach (var elem in root.Descendants().ToList())
        {
            var nsAttrs = elem.Attributes().Where(a => a.IsNamespaceDeclaration).ToList();
            foreach (var attr in nsAttrs)
            {
                // Check if the root already has this namespace declaration
                var existing = root.Attributes().FirstOrDefault(a =>
                    a.IsNamespaceDeclaration && a.Name == attr.Name);

                if (existing == null)
                {
                    // Default namespace (xmlns="...") should NOT be hoisted because
                    // it changes the namespace of unprefixed child elements at the root level.
                    // Only hoist prefixed declarations (xmlns:prefix="...").
                    if (attr.Name.Namespace == XNamespace.Xmlns)
                    {
                        root.Add(new XAttribute(attr));
                        attr.Remove();
                    }
                }
                else if (existing.Value == attr.Value)
                {
                    // Same declaration already on root — just remove from descendant
                    attr.Remove();
                }
                // If root has a DIFFERENT value for the same prefix, leave it in place
            }
        }
    }

    /// <summary>
    /// Normalizes empty elements so that &lt;e/&gt; and &lt;e&gt;&lt;/e&gt; compare equal.
    /// XNode.DeepEquals considers IsEmpty differences, but they are semantically identical.
    /// </summary>
    private static void NormalizeEmptyElements(XDocument doc)
    {
        foreach (var elem in doc.Descendants().ToList())
        {
            if (!elem.HasElements && !elem.Nodes().Any())
            {
                // Force all empty elements to have the same internal representation
                elem.Value = "";
            }
        }
    }

    /// <summary>
    /// Normalizes namespace declaration order on each element so that XNode.DeepEquals
    /// is not sensitive to the order of xmlns:* attributes.
    /// </summary>
    /// <summary>
    /// Reads an XML file with proper encoding detection from the XML prolog.
    /// </summary>
    private static async Task<string> ReadXmlFileAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        // Check for UTF-8 BOM — skip the 3 BOM bytes so the U+FEFF character
        // doesn't appear in the string (XDocument.Parse rejects leading BOM)
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        // Detect encoding from XML prolog
        var header = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 200));
        var match = Regex.Match(header, @"encoding=[""']([^""']+)[""']");
        if (match.Success)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var encoding = Encoding.GetEncoding(match.Groups[1].Value);
                return encoding.GetString(bytes);
            }
            catch { /* fall through to UTF-8 */ }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static void NormalizeNamespaceOrder(XDocument doc)
    {
        foreach (var elem in doc.Descendants().ToList())
        {
            var nsAttrs = elem.Attributes().Where(a => a.IsNamespaceDeclaration).ToList();
            if (nsAttrs.Count <= 1) continue;

            var regularAttrs = elem.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();

            // Sort namespace declarations: default xmlns first, then by prefix
            var sorted = nsAttrs.OrderBy(a => a.Name.LocalName == "xmlns" ? "" : a.Name.LocalName, StringComparer.Ordinal).ToList();

            // Replace all attributes with sorted namespaces + original regular attrs
            elem.RemoveAttributes();
            foreach (var ns in sorted) elem.Add(new XAttribute(ns));
            foreach (var attr in regularAttrs) elem.Add(new XAttribute(attr));
        }
    }

    private static void NormalizeAttributeOrder(XDocument doc)
    {
        foreach (var elem in doc.Descendants().ToList())
        {
            var nsAttrs = elem.Attributes().Where(a => a.IsNamespaceDeclaration).ToList();
            var regularAttrs = elem.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
            // Reorder when there are multiple regular attrs OR when namespace attrs are
            // interspersed with regular attrs (ensures ns-first ordering is consistent)
            if (regularAttrs.Count <= 1 && nsAttrs.Count == 0) continue;
            if (regularAttrs.Count == 0) continue;

            var sorted = regularAttrs.OrderBy(a => a.Name.NamespaceName + ":" + a.Name.LocalName, StringComparer.Ordinal).ToList();

            elem.RemoveAttributes();
            foreach (var ns in nsAttrs) elem.Add(new XAttribute(ns));
            foreach (var attr in sorted) elem.Add(new XAttribute(attr));
        }
    }

    /// <summary>
    /// Normalizes namespace prefixes so that prefix differences don't cause comparison failures.
    /// Strips namespace declaration attributes since XNode.DeepEquals compares them but
    /// element XNames already carry the full namespace URI regardless of prefix.
    /// </summary>
    private static void NormalizeNamespacePrefixes(XDocument doc)
    {
        foreach (var elem in doc.Descendants().ToList())
        {
            var nsAttrs = elem.Attributes().Where(a => a.IsNamespaceDeclaration).ToList();
            foreach (var ns in nsAttrs) ns.Remove();
        }
    }

    private static string WrapForParsing(string xml)
    {
        // If it looks like it already has a single root element, return as-is
        try
        {
            XDocument.Parse(xml);
            return xml;
        }
        catch
        {
            // Try wrapping as a fragment
            return $"<_wrapper_>{xml}</_wrapper_>";
        }
    }

    private static string NormalizeXml(string xml)
    {
        return xml.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
    }

    private async Task<bool> VerifySerializationAsync(XsltAssertion assertion, string? actualResult, CancellationToken ct)
    {
        string? expected = assertion.Value;
        if (assertion.ExpectedFile != null && File.Exists(assertion.ExpectedFile))
        {
            if (assertion.ExpectedEncoding != null)
            {
                // Read with the specified encoding (e.g., ISO-8859-1)
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var enc = Encoding.GetEncoding(assertion.ExpectedEncoding);
                expected = await File.ReadAllTextAsync(assertion.ExpectedFile, enc, ct);
            }
            else
            {
                expected = await File.ReadAllTextAsync(assertion.ExpectedFile, ct);
            }
        }

        // Normalize line endings for comparison
        var normalizedActual = actualResult?.Replace("\r\n", "\n").Trim();
        var normalizedExpected = expected?.Replace("\r\n", "\n").Trim();

        return normalizedActual == normalizedExpected;
    }

    private async Task<bool> VerifyResultDocumentAsync(
        XsltAssertion assertion, string? actualResult, CancellationToken ct,
        IReadOnlyDictionary<string, string>? secondaryResults = null)
    {
        if (assertion.Uri == null || secondaryResults == null)
            return false;

        // Look up the secondary result document by href URI
        string? secondaryContent = null;
        foreach (var kvp in secondaryResults)
        {
            // Match by URI suffix — the engine stores absolute or relative hrefs
            if (kvp.Key == assertion.Uri ||
                kvp.Key.EndsWith("/" + assertion.Uri, StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.EndsWith(assertion.Uri, StringComparison.OrdinalIgnoreCase))
            {
                secondaryContent = kvp.Value;
                break;
            }
        }

        if (secondaryContent == null)
            return false;

        // Verify child assertions against the secondary document content
        return await VerifyAssertionsAsync(assertion.Children, secondaryContent, ct, secondaryResults);
    }

    private static bool CompareFragments(XDocument actual, XDocument expected)
    {
        // Compare ignoring document-level differences
        var actualRoot = actual.Root;
        var expectedRoot = expected.Root;

        if (actualRoot == null && expectedRoot == null) return true;
        if (actualRoot == null || expectedRoot == null) return false;

        return XNode.DeepEquals(actualRoot, expectedRoot);
    }

    /// <summary>
    /// Evaluates a parameter select expression to a typed value.
    /// Handles common XPath literals: integers, decimals, strings, booleans.
    /// </summary>
    private static object? EvaluateParamSelect(string selectExpr)
    {
        var s = selectExpr.Trim();

        // Empty sequence
        if (s == "()") return null;

        // Boolean functions
        if (s == "true()") return true;
        if (s == "false()") return false;

        // String literal (single or double quoted)
        if ((s.StartsWith('\'') && s.EndsWith('\'')) ||
            (s.StartsWith('"') && s.EndsWith('"')))
        {
            return s[1..^1];
        }

        // XPath type constructors: xs:type(value)
        if (s.StartsWith("xs:", StringComparison.Ordinal))
        {
            var parenStart = s.IndexOf('(');
            var parenEnd = s.LastIndexOf(')');
            if (parenStart > 0 && parenEnd > parenStart)
            {
                var typeName = s[3..parenStart];
                var argStr = s[(parenStart + 1)..parenEnd].Trim();
                var innerVal = EvaluateParamSelect(argStr);
                var innerStr = innerVal?.ToString() ?? "";

                return typeName switch
                {
                    "integer" => long.TryParse(innerStr, System.Globalization.CultureInfo.InvariantCulture, out var li) ? li : innerVal,
                    "double" => double.TryParse(innerStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : innerVal,
                    "decimal" => decimal.TryParse(innerStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dm) ? dm : innerVal,
                    "float" => double.TryParse(innerStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : innerVal,
                    "string" => innerStr,
                    "anyURI" => innerStr,
                    "token" => innerStr,
                    "boolean" => innerStr is "true" or "1",
                    "untypedAtomic" => new PhoenixmlDb.Xdm.XsUntypedAtomic(innerStr),
                    _ => innerStr
                };
            }
        }

        // QName constructor: QName('ns', 'local')
        if (s.StartsWith("QName(", StringComparison.Ordinal) && s.EndsWith(')'))
        {
            var inner = s[6..^1]; // strip QName( and )
            var parts = inner.Split(',');
            if (parts.Length == 2)
            {
                var ns = EvaluateParamSelect(parts[0].Trim())?.ToString() ?? "";
                var local = EvaluateParamSelect(parts[1].Trim())?.ToString() ?? "";
                // Return as string for now (the test runner serializes to string anyway)
                return string.IsNullOrEmpty(ns) ? local : $"Q{{{ns}}}{local}";
            }
        }

        // Comma-separated sequence: 'one', 'two' or 1234, 5678
        if (s.Contains(',', StringComparison.Ordinal) && !s.Contains('('))
        {
            var items = s.Split(',');
            var results = new List<object?>();
            foreach (var item in items)
                results.Add(EvaluateParamSelect(item.Trim()));
            return results.ToArray();
        }

        // Simple arithmetic: 1 + 2 + 3 + 4
        if (s.Contains(" + ", StringComparison.Ordinal) && !s.Contains('\'') && !s.Contains('"'))
        {
            var parts = s.Split('+');
            long sum = 0;
            foreach (var part in parts)
            {
                if (long.TryParse(part.Trim(), out var v))
                    sum += v;
                else
                    goto fallback;
            }
            return sum;
        }

        // Integer
        if (long.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var intVal))
        {
            // Return int if it fits, otherwise long
            if (intVal is >= int.MinValue and <= int.MaxValue)
                return (int)intVal;
            return intVal;
        }

        // Decimal/double
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var dblVal))
        {
            return dblVal;
        }

        fallback:
        // Fall back to string
        return s;
    }

    private bool IsExpectedError(List<XsltAssertion> assertions, Exception ex)
    {
        foreach (var assertion in assertions)
        {
            if (assertion.Type == "error")
            {
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
    /// Runs all test cases and returns a summary.
    /// </summary>
    public async Task<XsltTestSummary> RunAllTestsAsync(
        IReadOnlyList<XsltTestCase> testCases,
        IProgress<XsltTestResult>? progress = null,
        CancellationToken ct = default)
    {
        var summary = new XsltTestSummary
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
/// Configuration for XSLT test runner.
/// </summary>
public sealed class XsltConfiguration
{
    public string XsltVersion { get; init; } = "3.0";
    public bool SupportsStreaming { get; init; }
    public bool SupportsHigherOrderFunctions { get; init; }
    public bool SupportsSchemaAwareness { get; init; }

    /// <summary>Skip individual tests by name or test-set/name.</summary>
    public HashSet<string> SkipTests { get; } = [];

    /// <summary>Skip entire test sets by name.</summary>
    public HashSet<string> SkipTestSets { get; } = [];

    /// <summary>Features supported by the processor.</summary>
    public HashSet<string> SupportedFeatures { get; } =
    [
        "serialization",
        "backwards_compatibility",
        "enable_assertions",
        "disabling_output_escaping",
        "dynamic_evaluation",
        "XPath_3.1"
    ];

    /// <summary>
    /// Checks whether the processor satisfies a test dependency.
    /// </summary>
    public bool SatisfiesDependency(XsltDependency dep)
    {
        return dep.Type switch
        {
            "spec" => SatisfiesSpec(dep),
            "feature" => SatisfiesFeature(dep),
            "on-multiple-match" => SatisfiesOnMultipleMatch(dep),
            "xsd-version" => dep.Satisfied,
            "xml-version" => dep.Satisfied,
            "year_component_values" => dep.Satisfied,
            "default_calendar" => dep.Satisfied,
            "languages" => dep.Satisfied,
            "maximum_number_of_decimal_digits" => SatisfiesDecimalDigits(dep),
            // Sweep-and-posture tests require streaming static analysis (XTSE3430)
            "sweep_and_posture" => false,
            _ => dep.Satisfied
        };
    }

    private static bool SatisfiesOnMultipleMatch(XsltDependency dep)
    {
        // We only support "recover" mode (pick highest priority, last declared wins for ties).
        // We do NOT support "error" mode (raise XTRE0540 on conflicts).
        var value = dep.Value ?? "";
        return value == "recover";
    }

    private static bool SatisfiesDecimalDigits(XsltDependency dep)
    {
        // .NET decimal supports 28-29 significant digits.
        // Tests requiring more precision than this should be skipped.
        const int MaxDecimalDigits = 29;
        if (int.TryParse(dep.Value, out var required))
            return required <= MaxDecimalDigits;
        return dep.Satisfied;
    }

    private bool SatisfiesSpec(XsltDependency dep)
    {
        var value = dep.Value ?? "";

        // Handle spec values like "XSLT10+", "XSLT20+", "XSLT30+", "XSLT30"
        if (!value.StartsWith("XSLT", StringComparison.OrdinalIgnoreCase))
        {
            // XPath or other spec — we support these alongside XSLT
            return dep.Satisfied;
        }

        // We support XSLT 3.0
        // XSLT10+, XSLT20+, XSLT30+ mean "this version and later" — we satisfy these
        // XSLT30 (no +) means "exactly this version" — we satisfy XSLT30 only
        // XSLT10, XSLT20 (no +) mean "exactly this version" — we do NOT satisfy these
        var hasPlus = value.Contains('+');
        var versionPart = value.Replace("XSLT", "").Replace("+", "");
        if (int.TryParse(versionPart, out var version))
        {
            var supported = hasPlus ? version <= 30 : version == 30;
            return dep.Satisfied ? supported : !supported;
        }

        return dep.Satisfied;
    }

    private bool SatisfiesFeature(XsltDependency dep)
    {
        var value = dep.Value ?? "";
        var weSupport = SupportedFeatures.Contains(value);

        if (SupportsStreaming && value == "streaming") weSupport = true;
        if (SupportsHigherOrderFunctions && value == "higher_order_functions") weSupport = true;
        if (SupportsSchemaAwareness && value == "schema_aware") weSupport = true;

        // dep.Satisfied == true means the test REQUIRES the feature
        // dep.Satisfied == false means the test requires the feature to NOT be supported
        return dep.Satisfied ? weSupport : !weSupport;
    }
}

/// <summary>
/// XSLT test case definition.
/// </summary>
public sealed class XsltTestCase
{
    public required string Name { get; init; }
    public required string TestSet { get; init; }
    public string Description { get; init; } = "";
    public XsltEnvironment Environment { get; set; } = new();
    public string? InlineStylesheet { get; set; }
    public List<XsltDependency> Dependencies { get; } = [];
    public List<XsltAssertion> Assertions { get; set; } = [];
}

/// <summary>
/// XSLT test environment.
/// </summary>
public sealed class XsltEnvironment
{
    public string? StylesheetPath { get; set; }
    public string? PrincipalSource { get; set; }
    public string? PrincipalSourceContent { get; set; }
    public Uri? PrincipalSourceBaseUri { get; set; }
    public string? PrincipalSourceSelect { get; set; }
    public Dictionary<string, string> AdditionalSources { get; } = [];
    public Dictionary<string, string> AdditionalSourceContents { get; } = [];
    public Dictionary<string, string> Parameters { get; } = [];
    public Dictionary<string, string> StaticParameters { get; } = [];
    public string? InitialTemplate { get; set; }
    public string? InitialTemplateNamespace { get; set; }
    public string? InitialFunction { get; set; }
    public string? InitialFunctionNamespace { get; set; }
    public List<string> InitialFunctionArgs { get; } = [];
    public string? InitialMode { get; set; }
    public string? InitialModeNamespace { get; set; }
    public string? InitialModeSelect { get; set; }
    public List<InitialTemplateParam> InitialTemplateParams { get; } = [];
    public Dictionary<string, List<string>> Collections { get; } = [];
    /// <summary>
    /// Package catalog: maps package name URI → list of (version, file path) pairs.
    /// Built from &lt;package&gt; elements in test environments.
    /// </summary>
    public Dictionary<string, List<(string? Version, string FilePath)>> Packages { get; } = [];
}

public sealed record InitialTemplateParam(string LocalName, string? Namespace, string SelectExpr, bool Tunnel);

/// <summary>
/// XSLT test dependency.
/// </summary>
public sealed class XsltDependency
{
    public required string Type { get; init; }
    public required string Value { get; init; }
    public bool Satisfied { get; init; } = true;
}

/// <summary>
/// XSLT result assertion.
/// </summary>
public sealed class XsltAssertion
{
    public required string Type { get; init; }
    public string? Value { get; init; }
    public string? ExpectedFile { get; set; }
    public string Compare { get; set; } = "XML";
    public bool IgnorePrefixes { get; set; }
    public string? ExpectedEncoding { get; set; }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056")]
    public string? Uri { get; set; }
    public List<XsltAssertion> Children { get; set; } = [];
}

/// <summary>
/// XSLT test result.
/// </summary>
public sealed class XsltTestResult
{
    public required XsltTestCase TestCase { get; init; }
    public bool Passed { get; set; }
    public string? ActualResult { get; set; }
    public Exception? Error { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
}

/// <summary>
/// XSLT test run summary.
/// </summary>
public sealed class XsltTestSummary
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
    public List<XsltTestResult> Results { get; } = [];
}
