using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

public sealed partial class StylesheetParser
{

    /// <summary>
    /// Parses an XSLT stylesheet from a string.
    /// </summary>
    public XsltStylesheet Parse(string xml, Uri? baseUri = null, Dictionary<string, string>? externalStaticParams = null,
        bool isLibraryPackage = false)
    {
        ArgumentNullException.ThrowIfNull(xml);
        // Strip leading BOM character (U+FEFF) that may be present when reading
        // UTF-8 files with BOM — XDocument.Parse rejects it as invalid content
        if (xml.Length > 0 && xml[0] == '\uFEFF')
            xml = xml[1..];
        _baseUri = baseUri;
        XDocument doc;
        // Always go through XmlReader when a base URI is available so XElement.BaseUri is
        // populated — that's what diagnostics surface as the originating module path. The
        // DTD-processing branch is the same code path with extra settings.
        if (baseUri != null)
        {
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = AllowDtdProcessing && xml.Contains("<!DOCTYPE", StringComparison.Ordinal)
                    ? System.Xml.DtdProcessing.Parse
                    : System.Xml.DtdProcessing.Prohibit,
                MaxCharactersFromEntities = 1_000_000,
            };
            if (settings.DtdProcessing == System.Xml.DtdProcessing.Parse)
                settings.XmlResolver = new System.Xml.XmlUrlResolver();
            using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml), settings, baseUri.AbsoluteUri);
            doc = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.SetBaseUri | LoadOptions.PreserveWhitespace);
        }
        else
        {
            doc = XDocument.Parse(xml, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        // Pre-pass with XmlReader to capture original element prefixes.
        // LINQ to XML loses prefix info when multiple prefixes map to the same namespace.
        _elementPrefixMap = BuildElementPrefixMap(xml);

        _externalStaticParams = externalStaticParams;
        ResolveShadowAttributes(doc.Root!, externalStaticParams, baseUri);

        // Pre-populate _staticVariables with externally-provided static param values
        // so that ParseStylesheet skips select evaluation for these params
        PopulateExternalStaticParams(externalStaticParams);

        var stylesheet = ParseStylesheet(doc.Root!);
        // Store the package catalog on the stylesheet for fn:transform package-name resolution
        if (_packageCatalog != null)
            stylesheet.PackageCatalog = _packageCatalog;
        // Validate decimal formats at the top level, after all imports have been resolved.
        // This must happen here (not in ParseStylesheet) because imported stylesheets may have
        // same-precedence conflicts that are overridden by higher-precedence declarations.
        ValidateDecimalFormats(stylesheet);
        ValidateAttributeSetReferences(stylesheet);
        if (!isLibraryPackage)
            ValidateNoAbstractComponents(stylesheet);
        ValidateOutputCharacterMapReferences(stylesheet);
        ValidatePatternFunctionReferences(stylesheet);
        ValidateStreamableTemplates(stylesheet);
        return stylesheet;
    }


    /// <summary>
    /// Parses an XSLT stylesheet from a stream.
    /// </summary>
    public XsltStylesheet Parse(Stream stream, Uri? baseUri = null, Dictionary<string, string>? externalStaticParams = null)
    {
        _baseUri = baseUri;
        var doc = XDocument.Load(stream, LoadOptions.SetLineInfo);
        _externalStaticParams = externalStaticParams;
        ResolveShadowAttributes(doc.Root!, externalStaticParams);

        // Pre-populate _staticVariables with externally-provided static param values
        // so that ParseStylesheet skips select evaluation for these params
        PopulateExternalStaticParams(externalStaticParams);

        var stylesheet = ParseStylesheet(doc.Root!);
        ValidateDecimalFormats(stylesheet);
        ValidateAttributeSetReferences(stylesheet);
        ValidatePatternFunctionReferences(stylesheet);
        ValidateStreamableTemplates(stylesheet);
        return stylesheet;
    }


    private static string? TryReadFilePackageVersion(string filePath)
    {
        try
        {
            if (!System.IO.File.Exists(filePath)) return null;
            var doc = XDocument.Load(filePath, LoadOptions.None);
            return doc.Root?.Attribute("package-version")?.Value;
        }
        catch (System.IO.IOException) { return null; }
        catch (System.Xml.XmlException) { return null; }
    }


    private XsltStylesheet? LoadExternalStylesheet(XElement element)
    {
        var hrefAttr = element.Attribute("href");
        // XTSE0010: href is a required attribute on xsl:import and xsl:include
        if (hrefAttr == null || string.IsNullOrEmpty(hrefAttr.Value))
            throw new XsltException("XTSE0010: xsl:import/xsl:include must have an href attribute",
                GetSourceLocation(element));

        var href = hrefAttr.Value;

        // Strip fragment identifier for embedded stylesheet references (§3.11.2)
        string? fragmentId = null;
        var hrefPath = href;
        var hashIndex = href.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex >= 0)
        {
            fragmentId = href[(hashIndex + 1)..];
            hrefPath = href[..hashIndex];
        }

        // Determine effective base URI for resolution.
        // DTD entity expansion may give elements a different BaseUri than _baseUri.
        // xsl:import/@href and xsl:include/@href resolve against the EFFECTIVE base URI of the
        // element carrying them, which xml:base overrides (XML Base; XSLT 3.0 §3.2). XElement's
        // BaseUri property reports the base of the DOCUMENT the element was read from and knows
        // nothing about xml:base attributes, so
        //     <xsl:include href="demo.xsl" xml:base="../../tutorial/coverage/"/>
        // resolved against the including module's own directory and failed as XTSE0165 even
        // though the target existed exactly where xml:base pointed (xspec issue 1135).
        var effectiveBase = ResolveEffectiveBaseUri(element);
        if (effectiveBase == null || ReferenceEquals(effectiveBase, _baseUri))
        {
            // No xml:base in scope — fall back to the document the element was read from, which
            // differs from _baseUri when this module was itself pulled in from elsewhere.
            if (!string.IsNullOrEmpty(element.BaseUri) &&
                Uri.TryCreate(element.BaseUri, UriKind.Absolute, out var elementBaseUri) &&
                (_baseUri == null || elementBaseUri.AbsoluteUri != _baseUri.AbsoluteUri))
            {
                effectiveBase = elementBaseUri;
            }
        }

        // Resolve href against base URI. Two paths from here on:
        //  - file:// (or unspecified): resolve to a local path and read with File.ReadAllText
        //  - http(s)://: fetch with HttpClient. We treat HTTP imports as opt-in by URI scheme —
        //    if the entry stylesheet was loaded over HTTPS, its imports come over HTTPS too.
        //    This mirrors how Saxon resolves imports against the entry's system id.
        Uri? resolvedUri = null;
        string? resolvedPath = null;
        if (effectiveBase != null)
        {
            try
            {
                resolvedUri = new Uri(effectiveBase, hrefPath);
                if (resolvedUri.IsFile)
                    resolvedPath = resolvedUri.LocalPath;
            }
            catch (UriFormatException)
            {
                // Fall through to try direct path
            }
        }

        var isHttp = resolvedUri != null && (resolvedUri.Scheme == Uri.UriSchemeHttp || resolvedUri.Scheme == Uri.UriSchemeHttps);

        if (!isHttp && (resolvedPath == null || !File.Exists(resolvedPath)))
        {
            // Try as relative path from base URI directory
            if (effectiveBase?.IsFile == true)
            {
                var baseDir = Path.GetDirectoryName(effectiveBase.LocalPath);
                if (baseDir != null)
                {
                    resolvedPath = Path.Combine(baseDir, hrefPath);
                }
            }
        }

        if (!isHttp && (resolvedPath == null || !File.Exists(resolvedPath)))
            throw new XsltException($"XTSE0165: Cannot find stylesheet module '{href}'",
                GetSourceLocation(element));

        // Recursion key: the absolute URI for HTTP imports, the canonical full path for file imports.
        var recursionKey = isHttp ? resolvedUri!.AbsoluteUri : Path.GetFullPath(resolvedPath!);
        // The element that closes the cycle decides the code: XTSE0180 for a module that
        // includes itself, XTSE0210 for one that imports itself. This method serves both,
        // and reported XTSE0210 for include cycles too.
        if (!_loadedStylesheets.Add(recursionKey))
        {
            throw element.Name.LocalName == "include"
                ? new XsltException($"XTSE0180: Stylesheet module '{href}' directly or indirectly includes itself",
                    GetSourceLocation(element))
                : new XsltException($"XTSE0210: Stylesheet module '{href}' directly or indirectly imports itself",
                    GetSourceLocation(element));
        }

        // Resource policy: check import access and try custom resolver
        string? policyResolvedXml = null;
        if (ResourcePolicy != null)
        {
            var importUri = isHttp ? resolvedUri! : new Uri(recursionKey);
            if (!ResourcePolicy.IsAllowed(importUri, PhoenixmlDb.XQuery.Security.ResourceAccessKind.ImportStylesheet))
                throw new XsltException(
                    $"XTSE0165: Resource policy denied import access to '{href}'",
                    GetSourceLocation(element));

            policyResolvedXml = ResourcePolicy.ResourceResolver?.ResolveStylesheetModule(href, _baseUri);
        }

        try
        {
            string xml;
            if (policyResolvedXml != null)
                xml = policyResolvedXml;
            else if (isHttp)
            {
                // Consult the preload cache first: callers running on Blazor
                // WebAssembly (or any runtime that disallows monitor-waits) must
                // supply pre-fetched content this way, since the synchronous
                // HttpClient call below blocks the calling thread to completion.
                if (PreloadedResources is { } preloaded && preloaded.TryGet(resolvedUri!, out var preloadedXml))
                {
                    xml = preloadedXml;
                }
                else if (OperatingSystem.IsBrowser())
                {
                    throw new XsltException(
                        $"XTSE0165: Cannot fetch xsl:import/xsl:include '{href}' on Blazor WebAssembly: " +
                        "synchronous HTTP I/O is not supported. Pre-fetch the imported stylesheet " +
                        "asynchronously and pass it through PreloadedResources to LoadStylesheetAsync.",
                        GetSourceLocation(element));
                }
                else
                {
                    xml = HttpResourceLoader.GetStringSync(resolvedUri!);
                }
            }
            else
                xml = File.ReadAllText(resolvedPath!);
            var savedBaseUri = _baseUri;
            var savedDefaultMode = _currentDefaultMode;
            _baseUri = isHttp ? resolvedUri! : new Uri(recursionKey);
            // Always load imported/included modules through XmlReader so SetBaseUri can
            // populate XElement.BaseUri with the imported module's URI — that's what
            // diagnostics later surface as "this error came from <module>".
            var importSettings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = AllowDtdProcessing && xml.Contains("<!DOCTYPE", StringComparison.Ordinal)
                    ? System.Xml.DtdProcessing.Parse
                    : System.Xml.DtdProcessing.Prohibit,
                MaxCharactersFromEntities = 1_000_000,
            };
            if (importSettings.DtdProcessing == System.Xml.DtdProcessing.Parse)
                importSettings.XmlResolver = new System.Xml.XmlUrlResolver();
            using var importReader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml), importSettings, _baseUri.AbsoluteUri);
            var doc = XDocument.Load(importReader, LoadOptions.SetLineInfo | LoadOptions.SetBaseUri | LoadOptions.PreserveWhitespace);
            // For embedded stylesheets, find the element with matching id (§3.11.2)
            XElement stylesheetRoot;
            if (fragmentId != null)
            {
                XNamespace xmlNs = "http://www.w3.org/XML/1998/namespace";
                stylesheetRoot = doc.Descendants()
                    .FirstOrDefault(e =>
                        e.Attribute("id")?.Value == fragmentId ||
                        e.Attribute(xmlNs + "id")?.Value == fragmentId)
                    ?? throw new XsltException(
                        $"XTSE0165: Cannot find embedded stylesheet with id '{fragmentId}' in '{hrefPath}'",
                        GetSourceLocation(element));

                // Verify it's an xsl:stylesheet or xsl:transform element
                if (stylesheetRoot.Name.Namespace != XsltNs ||
                    stylesheetRoot.Name.LocalName is not ("stylesheet" or "transform"))
                    throw new XsltException(
                        $"XTSE0165: Element with id '{fragmentId}' is not an xsl:stylesheet/xsl:transform element",
                        GetSourceLocation(element));
            }
            else
            {
                stylesheetRoot = doc.Root!;
                // XTSE0165: the target of xsl:import / xsl:include must be a stylesheet
                // module (xsl:stylesheet / xsl:transform, or a simplified literal-result
                // stylesheet). An xsl:package is NOT a stylesheet module and cannot be
                // imported or included — it is consumed only via xsl:use-package. See
                // W3C decl/package package-910.
                if (stylesheetRoot.Name.Namespace == XsltNs
                    && stylesheetRoot.Name.LocalName == "package")
                    throw new XsltException(
                        $"XTSE0165: The target of xsl:import/xsl:include '{href}' is an xsl:package, which is not a stylesheet module",
                        GetSourceLocation(element));
            }

            // Handle xml:base on embedded stylesheet elements
            if (fragmentId != null)
            {
                XNamespace xmlNs = "http://www.w3.org/XML/1998/namespace";
                var xmlBase = stylesheetRoot.Attribute(xmlNs + "base")?.Value;
                if (xmlBase != null)
                {
                    _baseUri = new Uri(_baseUri!, xmlBase);
                }
            }

            // Pass the importer's external static params: a shadow attribute in an imported
            // module is resolved against the parameters the CALLER supplied, not just this
            // module's defaults.
            ResolveShadowAttributes(stylesheetRoot, _externalStaticParams);
            // Check use-when on the root xsl:stylesheet/xsl:transform element
            if (!ShouldIncludeElement(stylesheetRoot))
                return null;
            // Save and replace _elementPrefixMap with one built from THIS module's xml.
            // The map is keyed by (line, col) which are module-relative; querying entries
            // built from the entry stylesheet against XElements from an included module
            // returns wrong prefixes (Docbook TNG: head.xsl `<link>` LREs were getting
            // prefix `xsl` because line 190 of head.xsl matched line 190 of docbook.xsl
            // in the entry stylesheet's map — different file, same coordinates).
            var savedPrefixMap = _elementPrefixMap;
            _elementPrefixMap = BuildElementPrefixMap(xml);
            XsltStylesheet? result;
            try
            {
                result = ParseStylesheet(stylesheetRoot, isTopLevel: false);
            }
            finally
            {
                _elementPrefixMap = savedPrefixMap;
            }
            _baseUri = savedBaseUri;
            _currentDefaultMode = savedDefaultMode;
            return result;
        }
        catch (IOException ex)
        {
            throw new XsltException($"XTSE0165: Cannot load stylesheet module '{href}': {ex.Message}",
                GetSourceLocation(element));
        }
        catch (XmlException ex)
        {
            throw new XsltException($"XTSE0165: Invalid XML in stylesheet module '{href}': {ex.Message}",
                GetSourceLocation(element));
        }
        // Let XsltException propagate — errors in imported/included stylesheets
        // should cause compilation to fail (XTSE0010, XTSE0090, etc.)
        finally
        {
            _loadedStylesheets.Remove(recursionKey);
        }
    }


    /// <summary>
    /// Resolves and reads the serialization-parameters document, returning its
    /// <c>output:serialization-parameters</c> root element. The href is resolved against the base URI
    /// of the module that declared the <c>xsl:output</c> (so a document in a subdirectory beside an
    /// included module resolves correctly — output-0722).
    /// </summary>
    private XElement LoadSerializationParameterDocument(XElement outputElement, string href)
    {
        Uri? baseUri = null;
        if (!string.IsNullOrEmpty(outputElement.BaseUri) &&
            Uri.TryCreate(outputElement.BaseUri, UriKind.Absolute, out var elementBase))
            baseUri = elementBase;
        baseUri ??= _baseUri;

        Uri resolved;
        if (baseUri != null)
            resolved = new Uri(baseUri, href);
        else if (!Uri.TryCreate(href, UriKind.Absolute, out resolved!))
            throw new XsltException(
                $"XTSE0010: Cannot resolve serialization parameter document '{href}' without a base URI",
                GetSourceLocation(outputElement));

        if (ResourcePolicy != null &&
            !ResourcePolicy.IsAllowed(resolved, PhoenixmlDb.XQuery.Security.ResourceAccessKind.ReadDocument))
            throw new XsltException(
                $"XTSE0010: Resource policy denied access to serialization parameter document '{href}'",
                GetSourceLocation(outputElement));

        string xml;
        try
        {
            if (resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps)
            {
                if (PreloadedResources is { } preloaded && preloaded.TryGet(resolved, out var preloadedXml))
                    xml = preloadedXml;
                else if (OperatingSystem.IsBrowser())
                    throw new XsltException(
                        $"XTSE0010: Cannot fetch serialization parameter document '{href}' on Blazor WebAssembly synchronously.",
                        GetSourceLocation(outputElement));
                else
                    xml = HttpResourceLoader.GetStringSync(resolved);
            }
            else
            {
                xml = File.ReadAllText(resolved.LocalPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new XsltException(
                $"XTSE0010: Cannot read serialization parameter document '{href}': {ex.Message}",
                GetSourceLocation(outputElement));
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new XsltException(
                $"XTSE0010: Serialization parameter document '{href}' is not well-formed: {ex.Message}",
                GetSourceLocation(outputElement));
        }

        var root = doc.Root
            ?? throw new XsltException(
                $"XTSE0010: Serialization parameter document '{href}' is empty",
                GetSourceLocation(outputElement));
        if (root.Name.Namespace != SerializationParamsNs || root.Name.LocalName != "serialization-parameters")
            throw new XsltException(
                $"XTSE0010: Serialization parameter document '{href}' root must be output:serialization-parameters",
                GetSourceLocation(outputElement));
        return root;
    }


    private XsltIf ParseIf(XElement element, SourceLocation? location)
    {
        var testAttr = element.Attribute("test")
            ?? throw new XsltException("XTSE0010: xsl:if requires a 'test' attribute", location);
        var test = ParseExpr(testAttr.Value, testAttr);

        return new XsltIf
        {
            Location = location,
            Test = test,
            Then = ParseSequenceConstructor(element)
        };
    }


    private XsltProcessingInstruction ParsePI(XElement element, SourceLocation? location)
    {
        var name = ParseAvt(element.Attribute("name")!.Value, element, element.Attribute("name"));
        var selectAttr = element.Attribute("select");

        // XTSE0880: select and non-empty content are mutually exclusive
        ValidateSelectContentExclusive(selectAttr, element, "XTSE0880", "xsl:processing-instruction", location);

        return new XsltProcessingInstruction
        {
            Location = location,
            Name = name,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.Nodes().Any()
                ? ParseSequenceConstructor(element)
                : null
        };
    }


    /// <summary>
    /// Attempts to parse a key() function call pattern.
    /// Returns a KeyPattern if the pattern starts with "key(", otherwise null.
    /// Handles: key('name', value), key('name', $var), key('name', value)//child, etc.
    /// </summary>
    private KeyPattern? TryParseKeyPattern(string pattern, XElement context)
    {
        if (!pattern.StartsWith("key(", StringComparison.Ordinal))
            return null;

        // Find the matching closing parenthesis for key(...)
        // Start at index 4 (after "key(") to scan inside the parentheses
        var depth = 0;
        var closeIdx = -1;
        for (var k = 4; k < pattern.Length; k++)
        {
            var c = pattern[k];
            if (c == '(') depth++;
            else if (c == ')')
            {
                if (depth == 0) { closeIdx = k; break; }
                depth--;
            }
            else if (c == '\'' || c == '"')
            {
                var q = c;
                k++;
                while (k < pattern.Length && pattern[k] != q) k++;
            }
        }

        if (closeIdx < 0)
            return null;

        // Extract the two arguments: key('name', value)
        var argsStr = pattern[4..closeIdx];
        var (keyName, valueExpr) = ParseKeyPatternArgs(argsStr, context);

        if (keyName == null)
            return null;

        // Check for continuation path: key(...)//p or key(...)/p
        var rest = pattern[(closeIdx + 1)..].TrimStart();
        if (rest.Length == 0)
        {
            return new KeyPattern
            {
                KeyName = keyName,
                ValueExpression = valueExpr!
            };
        }

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            var continuationStr = rest[2..];
            return new KeyPattern
            {
                KeyName = keyName,
                ValueExpression = valueExpr!,
                Continuation = ParsePattern(continuationStr, context),
                DescendantSeparator = true
            };
        }

        if (rest.StartsWith('/'))
        {
            var continuationStr = rest[1..];
            return new KeyPattern
            {
                KeyName = keyName,
                ValueExpression = valueExpr!,
                Continuation = ParsePattern(continuationStr, context),
                DescendantSeparator = false
            };
        }

        // Unrecognized continuation — not a key pattern
        return null;
    }


    private IdPattern? TryParseIdPattern(string pattern, XElement context)
    {
        if (!pattern.StartsWith("id(", StringComparison.Ordinal))
            return null;

        // Make sure it's not generate-id( or some other function ending in "id("
        // by checking the character before "id(" is not a letter/digit
        // Actually, since we trim the pattern, "id(" at position 0 is always valid.

        // Find the matching closing parenthesis for id(...)
        var depth = 0;
        var closeIdx = -1;
        for (var k = 3; k < pattern.Length; k++)
        {
            var c = pattern[k];
            if (c == '(') depth++;
            else if (c == ')')
            {
                if (depth == 0) { closeIdx = k; break; }
                depth--;
            }
            else if (c == '\'' || c == '"')
            {
                var q = c;
                k++;
                while (k < pattern.Length && pattern[k] != q) k++;
            }
        }

        if (closeIdx < 0)
            return null;

        // Extract the argument: id(value)
        var argStr = pattern[3..closeIdx].Trim();
        XQueryExpression valueExpr;
        try
        {
            valueExpr = ParseExpr(argStr);
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031

        // Check for continuation path: id(...)//p or id(...)/p
        var rest = pattern[(closeIdx + 1)..].TrimStart();
        if (rest.Length == 0)
        {
            return new IdPattern { ValueExpression = valueExpr };
        }

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            return new IdPattern
            {
                ValueExpression = valueExpr,
                Continuation = ParsePattern(rest[2..], context),
                DescendantSeparator = true
            };
        }

        if (rest.StartsWith('/'))
        {
            return new IdPattern
            {
                ValueExpression = valueExpr,
                Continuation = ParsePattern(rest[1..], context),
                DescendantSeparator = false
            };
        }

        // Unrecognized continuation — not an id pattern
        return null;
    }


    /// <summary>
    /// Parses a pattern that is a single parenthesized group, optionally followed by outer
    /// predicates. "(P)" unwraps to P; "(P)[pred]" becomes a
    /// <see cref="ParenthesizedPositionalPattern"/> whose predicate filters the whole
    /// sequence matching P (document order). See W3C match-076 and match-215.
    /// Returns null when the pattern is not a single wrapped group (e.g. "(a)/b").
    /// </summary>
    private XsltPattern? TryParseParenthesizedPositionalPattern(string pattern, XElement context)
    {
        if (pattern.Length < 3 || pattern[0] != '(')
            return null;

        // Find the parenthesis that closes the leading '(' (respecting nesting and strings).
        var depth = 0;
        var closeIdx = -1;
        for (var k = 0; k < pattern.Length; k++)
        {
            var c = pattern[k];
            if (c == '\'' || c == '"')
            {
                var q = c;
                k++;
                while (k < pattern.Length && pattern[k] != q) k++;
            }
            else if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) { closeIdx = k; break; }
            }
        }
        if (closeIdx < 0)
            return null;

        var rest = pattern[(closeIdx + 1)..].TrimStart();

        // Fully parenthesized "(P)" with nothing after — unwrap to P, but mark path patterns
        // so the child-or-top rule is disabled (a parenthesized pattern is an expression).
        if (rest.Length == 0)
        {
            var innerOnly = pattern[1..closeIdx].Trim();
            return innerOnly.Length == 0 ? null : MarkParenthesized(ParsePattern(innerOnly, context));
        }

        if (!rest.StartsWith('['))
            return null; // e.g. "(a)/b" — let other handlers deal with it

        // Collect the outer predicate expressions.
        var predicates = new List<XQueryExpression>();
        var predPart = rest;
        while (predPart.StartsWith('['))
        {
            var bracketDepth = 0;
            var k = 0;
            for (; k < predPart.Length; k++)
            {
                if (predPart[k] == '[') bracketDepth++;
                else if (predPart[k] == ']') { bracketDepth--; if (bracketDepth == 0) break; }
                else if (predPart[k] == '\'' || predPart[k] == '"')
                {
                    var q = predPart[k];
                    k++;
                    while (k < predPart.Length && predPart[k] != q) k++;
                }
            }
            if (k >= predPart.Length) return null; // unbalanced
            predicates.Add(ParseExpression(predPart[1..k], context));
            predPart = predPart[(k + 1)..].TrimStart();
        }
        if (predPart.Length != 0)
            return null; // trailing content after the predicates — not this shape

        var innerText = pattern[1..closeIdx].Trim();
        if (innerText.Length == 0)
            return null;

        var inner = MarkParenthesized(ParsePattern(innerText, context));
        return new ParenthesizedPositionalPattern { Inner = inner, Predicates = predicates };
    }


    private VariableReferencePattern? TryParseVariableReferencePattern(string pattern, XElement context)
    {
        if (pattern.Length < 2 || pattern[0] != '$')
            return null;

        // Extract the variable name: $name or $prefix:name or $Q{uri}name
        var nameStart = 1;
        var nameEnd = nameStart;

        // Handle EQName: $Q{uri}local
        if (pattern.Length > nameStart + 1 && pattern[nameStart] == 'Q' && pattern[nameStart + 1] == '{')
        {
            var closeBrace = pattern.IndexOf('}', nameStart + 2);
            if (closeBrace < 0) return null;
            nameEnd = closeBrace + 1;
            // Continue past the local name
            while (nameEnd < pattern.Length && (char.IsLetterOrDigit(pattern[nameEnd]) || pattern[nameEnd] == '_' || pattern[nameEnd] == '-' || pattern[nameEnd] == '.'))
                nameEnd++;
        }
        else
        {
            // Regular QName: $name or $prefix:name
            while (nameEnd < pattern.Length && (char.IsLetterOrDigit(pattern[nameEnd]) || pattern[nameEnd] == '_' || pattern[nameEnd] == '-' || pattern[nameEnd] == '.' || pattern[nameEnd] == ':'))
                nameEnd++;
        }

        if (nameEnd <= nameStart) return null;

        var varNameStr = pattern[nameStart..nameEnd];
        var varName = ParseQName(varNameStr, context);

        // Check for continuation path: $var//path or $var/path, or a predicate: $var[pred]
        var rest = pattern[nameEnd..].TrimStart();
        if (rest.Length == 0)
        {
            return new VariableReferencePattern { VariableName = varName };
        }

        // $var[pred1][pred2]... — trailing predicate(s) directly on the variable reference.
        if (rest.StartsWith('['))
        {
            var predicates = new List<XQueryExpression>();
            var predPart = rest;
            while (predPart.StartsWith('['))
            {
                var bracketDepth = 0;
                var k = 0;
                for (; k < predPart.Length; k++)
                {
                    if (predPart[k] == '[') bracketDepth++;
                    else if (predPart[k] == ']') { bracketDepth--; if (bracketDepth == 0) break; }
                    else if (predPart[k] == '\'' || predPart[k] == '"')
                    {
                        var q = predPart[k];
                        k++;
                        while (k < predPart.Length && predPart[k] != q) k++;
                    }
                }
                if (k >= predPart.Length) return null; // unbalanced — not a variable pattern
                predicates.Add(ParseExpression(predPart[1..k], context));
                predPart = predPart[(k + 1)..].TrimStart();
            }
            if (predPart.Length != 0) return null; // trailing junk — not a clean $var[pred]
            return new VariableReferencePattern { VariableName = varName, Predicates = predicates };
        }

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            return new VariableReferencePattern
            {
                VariableName = varName,
                Continuation = ParsePattern(rest[2..], context),
                DescendantSeparator = true
            };
        }

        if (rest.StartsWith('/'))
        {
            return new VariableReferencePattern
            {
                VariableName = varName,
                Continuation = ParsePattern(rest[1..], context),
                DescendantSeparator = false
            };
        }

        // Unrecognized continuation — not a variable reference pattern
        return null;
    }


    private DocFunctionPattern? TryParseDocFunctionPattern(string pattern, XElement context)
    {
        // Match doc('uri') or doc("uri") at the start of a pattern
        if (!pattern.StartsWith("doc(", StringComparison.Ordinal) &&
            !pattern.StartsWith("doc (", StringComparison.Ordinal))
            return null;

        var openParen = pattern.IndexOf('(', StringComparison.Ordinal);
        if (openParen < 0) return null;

        // Find the matching closing paren
        var depth = 1;
        var i = openParen + 1;
        while (i < pattern.Length && depth > 0)
        {
            if (pattern[i] == '(') depth++;
            else if (pattern[i] == ')') depth--;
            else if (pattern[i] == '\'' || pattern[i] == '"')
            {
                var q = pattern[i];
                i++;
                while (i < pattern.Length && pattern[i] != q) i++;
            }
            i++;
        }
        if (depth != 0) return null;

        var closeParen = i - 1;
        var argsStr = pattern[(openParen + 1)..closeParen].Trim();

        // Extract the URI string argument — must be a string literal
        if (argsStr.Length < 2) return null;
        if ((argsStr[0] != '\'' && argsStr[0] != '"') || argsStr[^1] != argsStr[0])
            return null;
        var uri = argsStr[1..^1];

        // Resolve relative URI against the stylesheet base URI
        var baseUri = ResolveEffectiveBaseUri(context);
        if (baseUri != null)
        {
            try
            {
                var resolvedUri = new Uri(baseUri, uri);
                uri = resolvedUri.ToString();
            }
#pragma warning disable CA1031 // Intentional broad catch — see comment below
            catch (Exception)
            {
                // If the relative URI cannot be resolved against the stylesheet base URI at
                // compile time (e.g. unknown base URI, opaque URI scheme), keep the original
                // literal URI. It will be resolved at runtime by the document resolver.
            }
#pragma warning restore CA1031
        }

        // Check for continuation path
        var rest = pattern[(closeParen + 1)..].TrimStart();
        if (rest.Length == 0)
        {
            return new DocFunctionPattern { DocumentUri = uri };
        }

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            return new DocFunctionPattern
            {
                DocumentUri = uri,
                Continuation = ParsePattern(rest[2..], context),
                DescendantSeparator = true
            };
        }

        if (rest.StartsWith('/'))
        {
            return new DocFunctionPattern
            {
                DocumentUri = uri,
                Continuation = ParsePattern(rest[1..], context),
                DescendantSeparator = false
            };
        }

        // Unrecognized continuation
        return null;
    }

}
