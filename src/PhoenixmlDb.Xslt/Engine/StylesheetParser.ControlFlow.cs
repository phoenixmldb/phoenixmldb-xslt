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

    private XsltSequenceConstructor ParseSequenceConstructor(XElement element)
    {
        // Bound the nesting depth of the constructed instruction tree. ParseInstruction <->
        // ParseSequenceConstructor recurse one level per body, and — critically — every LATER pass
        // that walks the instruction tree (streamability classification, the executor at transform
        // time) recurses to the same depth. Guarding only the parser leaves those passes to
        // overflow the native stack on a pathologically deep (untrusted) stylesheet — an uncatchable
        // StackOverflowException that crashes the host. Capping the tree depth here bounds them all
        // at once; a stylesheet nested past the cap is rejected with a catchable compile error.
        if (++_sequenceConstructorDepth > MaxNestingDepth)
        {
            _sequenceConstructorDepth--;
            throw new XsltException(
                $"Stylesheet nesting depth exceeds the maximum of {MaxNestingDepth}.",
                GetSourceLocation(element));
        }

        try
        {
            return ParseSequenceConstructorCore(element);
        }
        finally
        {
            _sequenceConstructorDepth--;
        }
    }


    private XsltSequenceConstructor ParseSequenceConstructorCore(XElement element)
    {
        var instructions = new List<XsltInstruction>();
        var expandText = IsExpandTextActive(element);

        // Per XSLT spec: comments and PIs are stripped first, then for each
        // contiguous run of sibling text nodes, if ANY text node in the run is
        // non-whitespace, ALL text nodes in the run are retained. This handles
        // CDATA sections adjacent to whitespace and comments splitting text nodes.
        var nodes = element.Nodes().ToList();
        var isInsideText = element.Name == XsltNs + "text";
        var preserveSpace = IsXmlSpacePreserve(element);
        for (var ni = 0; ni < nodes.Count; ni++)
        {
            switch (nodes[ni])
            {
                case XText:
                    // Collect the entire contiguous run of text nodes (and comments/PIs)
                    var runStart = ni;
                    var hasNonWhitespace = false;
                    var sb = new System.Text.StringBuilder();
                    while (ni < nodes.Count && nodes[ni] is XText or XComment or XProcessingInstruction)
                    {
                        if (nodes[ni] is XText textNode)
                        {
                            sb.Append(textNode.Value);
                            if (!hasNonWhitespace && !IsXmlWhitespaceOnly(textNode.Value))
                                hasNonWhitespace = true;
                        }
                        // Comments and PIs are stripped (skipped)
                        ni++;
                    }
                    ni--; // Back up since the for loop will increment
                    var value = sb.ToString();

                    // Retain text if: any text in run is non-whitespace, inside xsl:text,
                    // or xml:space="preserve"
                    if (hasNonWhitespace || isInsideText || preserveSpace)
                    {
                        instructions.Add(CreateTextInstruction(value, expandText, element));
                    }
                    break;

                case XElement child:
                    instructions.Add(ParseInstruction(child));
                    break;
            }
        }

        // XTSE0010: xsl:on-empty must be the absolute final instruction in a sequence constructor.
        // Nothing — not even xsl:on-non-empty — may follow it.
        bool sawOnEmpty = false;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is XsltOnEmpty)
            {
                sawOnEmpty = true;
            }
            else if (sawOnEmpty)
            {
                throw new XsltException("XTSE0010: No instructions may follow xsl:on-empty in a sequence constructor", instructions[i].Location);
            }
        }

        return new XsltSequenceConstructor { Instructions = instructions };
    }


    private XsltForEach ParseForEach(XElement element, SourceLocation? location)
    {
        var select = ParseExpr(RequiredAttribute(element, "select").Value, element.Attribute("select"));

        var sorts = new List<XsltSort>();
        var bodyInstructions = new List<XsltInstruction>();
        var expandText = IsExpandTextActive(element);
        var preserveSpace = IsXmlSpacePreserve(element);
        var pastSorts = false;

        // Pre-scan to find where sort elements end, so we can correctly identify
        // body content for xml:space="preserve" whitespace handling
        var nodes = element.Nodes().ToList();
        int lastSortIndex = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is XElement child && child.Name == XsltNs + "sort" && ShouldIncludeElement(child))
                lastSortIndex = i;
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            switch (node)
            {
                case XElement child when child.Name == XsltNs + "sort":
                    if (!ShouldIncludeElement(child)) break;
                    if (pastSorts)
                        throw new XsltException("XTSE0010: xsl:sort elements must come before other content in xsl:for-each", location);
                    sorts.Add(ParseSort(child));
                    break;
                case XElement child:
                    pastSorts = true;
                    bodyInstructions.Add(ParseInstruction(child));
                    break;
                case XText text:
                    bool inBody = i > lastSortIndex;
                    if (!IsXmlWhitespaceOnly(text.Value))
                    {
                        pastSorts = true;
                        bodyInstructions.Add(CreateTextInstruction(text.Value, expandText, element));
                    }
                    else if (inBody && preserveSpace)
                    {
                        // xml:space="preserve": whitespace-only text nodes in the body
                        // (after all sort elements) are preserved per XSLT §4.3
                        bodyInstructions.Add(CreateTextInstruction(text.Value, expandText, element));
                    }
                    break;
            }
        }

        return new XsltForEach
        {
            Location = location,
            Select = select,
            Sorts = sorts,
            Body = new XsltSequenceConstructor { Instructions = bodyInstructions }
        };
    }


    private XsltForEachGroup ParseForEachGroup(XElement element, SourceLocation? location)
    {
        var select = ParseExpr(RequiredAttribute(element, "select").Value, element.Attribute("select"));
        var groupByAttr = element.Attribute("group-by");
        var groupAdjacentAttr = element.Attribute("group-adjacent");
        var groupStartingWithAttr = element.Attribute("group-starting-with");
        var groupEndingWithAttr = element.Attribute("group-ending-with");
        var collationAttr = element.Attribute("collation");
        var compositeAttr = element.Attribute("composite");

        var sorts = new List<XsltSort>();
        var bodyInstructions = new List<XsltInstruction>();
        var expandText = IsExpandTextActive(element);
        var preserveSpace = IsXmlSpacePreserve(element);

        var nodes = element.Nodes().ToList();
        int lastSortIndex = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is XElement child && child.Name == XsltNs + "sort" && ShouldIncludeElement(child))
                lastSortIndex = i;
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            switch (node)
            {
                case XElement child when child.Name == XsltNs + "sort":
                    if (ShouldIncludeElement(child))
                        sorts.Add(ParseSort(child));
                    break;
                case XElement child:
                    bodyInstructions.Add(ParseInstruction(child));
                    break;
                case XText text:
                    bool inBody = i > lastSortIndex;
                    if (!IsXmlWhitespaceOnly(text.Value))
                        bodyInstructions.Add(CreateTextInstruction(text.Value, expandText, element));
                    else if (inBody && preserveSpace)
                        bodyInstructions.Add(CreateTextInstruction(text.Value, expandText, element));
                    break;
            }
        }

        // XTSE1080: Exactly one grouping attribute must be present
        var groupingAttrCount = (groupByAttr != null ? 1 : 0)
                              + (groupAdjacentAttr != null ? 1 : 0)
                              + (groupStartingWithAttr != null ? 1 : 0)
                              + (groupEndingWithAttr != null ? 1 : 0);
        if (groupingAttrCount == 0)
            throw new XsltException("XTSE1080: xsl:for-each-group must have one of the attributes group-by, group-adjacent, group-starting-with, or group-ending-with", location);
        if (groupingAttrCount > 1)
            throw new XsltException("XTSE1080: xsl:for-each-group must not have more than one of the attributes group-by, group-adjacent, group-starting-with, or group-ending-with", location);

        // XTSE1090: collation only allowed with group-by or group-adjacent
        if (collationAttr != null && groupByAttr == null && groupAdjacentAttr == null)
            throw new XsltException("XTSE1090: The collation attribute of xsl:for-each-group may only be specified when group-by or group-adjacent is specified", location);

        // XTSE0020: composite attribute must be a valid xsl:yes-or-no value
        if (compositeAttr != null && ParseYesNo(compositeAttr) == null)
            throw new XsltException($"XTSE0020: Invalid value '{compositeAttr.Value}' for composite attribute", location);

        // XTSE1017: stable attribute only on first xsl:sort
        for (int i = 1; i < sorts.Count; i++)
        {
            if (sorts[i].Stable != null)
                throw new XsltException("XTSE1017: The stable attribute is permitted only on the first xsl:sort element within xsl:for-each-group", location);
        }

        return new XsltForEachGroup
        {
            Location = location,
            Select = select,
            GroupBy = groupByAttr != null ? ParseExpr(groupByAttr.Value, groupByAttr) : null,
            GroupAdjacent = groupAdjacentAttr != null ? ParseExpr(groupAdjacentAttr.Value, groupAdjacentAttr) : null,
            GroupStartingWith = groupStartingWithAttr != null ? ParsePattern(groupStartingWithAttr.Value, element) : null,
            GroupEndingWith = groupEndingWithAttr != null ? ParsePattern(groupEndingWithAttr.Value, element) : null,
            Collation = collationAttr != null ? ParseAvt(collationAttr.Value, element, collationAttr) : null,
            Composite = ParseYesNo(compositeAttr) ?? false,
            Sorts = sorts,
            Body = new XsltSequenceConstructor { Instructions = bodyInstructions }
        };
    }


    private XsltIterate ParseIterate(XElement element, SourceLocation? location)
    {
        var select = ParseExpr(RequiredAttribute(element, "select").Value, element.Attribute("select"));

        var parameters = new List<XsltParam>();
        XsltSequenceConstructor? onCompletion = null;
        var bodyInstructions = new List<XsltInstruction>();

        var expandText = IsExpandTextActive(element);

        // XTSE0010: the content of xsl:iterate is (xsl:param*, xsl:on-completion?, sequence
        // constructor) — in that order. Checked BEFORE the children are parsed, because a
        // misplaced xsl:param is otherwise reported by whatever its own body trips over first:
        // "$x is not defined" for a param whose select reads a variable declared above it
        // (iterate-008/901), or an attribute error inside a misplaced xsl:on-completion
        // (iterate-024).
        var seenNonParam = false;
        var seenOnCompletion = false;
        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child))
                continue;
            var isParam = child.Name == XsltNs + "param";
            var isOnCompletion = child.Name == XsltNs + "on-completion";
            if (isParam && seenNonParam)
                throw new XsltException(
                    "XTSE0010: xsl:param must come first in xsl:iterate, before any other content",
                    GetSourceLocation(child));
            if (isOnCompletion && seenOnCompletion)
                throw new XsltException(
                    "XTSE0010: xsl:iterate must not have more than one xsl:on-completion",
                    GetSourceLocation(child));
            if (isOnCompletion && seenNonParam)
                throw new XsltException(
                    "XTSE0010: xsl:on-completion must come before the body of xsl:iterate, after any xsl:param",
                    GetSourceLocation(child));
            seenOnCompletion |= isOnCompletion;
            seenNonParam |= !isParam;
        }

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child when child.Name == XsltNs + "param":
                    if (ShouldIncludeElement(child))
                        parameters.Add(ParseParam(child));
                    break;
                case XElement child when child.Name == XsltNs + "on-completion":
                {
                    var selectAttr = child.Attribute("select");
                    if (selectAttr != null && child.Nodes().Any())
                        throw new XsltException("XTSE3125: xsl:on-completion must not have both a select attribute and content", location);
                    if (selectAttr != null)
                    {
                        // on-completion with select: wrap as xsl:sequence instruction
                        var seqInstr = new XsltSequence
                        {
                            Location = GetSourceLocation(child),
                            Select = ParseExpr(selectAttr.Value, selectAttr)
                        };
                        onCompletion = new XsltSequenceConstructor { Instructions = new List<XsltInstruction> { seqInstr } };
                    }
                    else
                    {
                        onCompletion = ParseSequenceConstructor(child);
                    }
                    break;
                }
                case XElement child:
                    bodyInstructions.Add(ParseInstruction(child));
                    break;
                case XText text:
                    if (!IsXmlWhitespaceOnly(text.Value))
                        bodyInstructions.Add(CreateTextInstruction(text.Value, expandText, element));
                    break;
            }
        }

        // Validate: no duplicate param names (XTSE0580)
        var paramNames = new HashSet<QName>();
        foreach (var param in parameters)
        {
            if (!paramNames.Add(param.Name))
                throw new XsltException($"XTSE0580: Duplicate parameter name '{param.Name}' in xsl:iterate", location);
        }

        // XTSE3520: xsl:iterate parameters must have a default value (select or content)
        // Exception: if the type allows empty sequence (? or *), empty sequence is the implicit default
        foreach (var param in parameters)
        {
            if (param.Select == null && param.Content == null && !param.Required)
            {
                if (param.As != null && (param.As.Occurrence == PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrOne
                    || param.As.Occurrence == PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore))
                    continue; // Empty sequence is a valid default for optional types
                throw new XsltException($"XTSE3520: Parameter '{param.Name}' in xsl:iterate must have a select attribute or child content as a default value",
                    location);
            }
        }

        var result = new XsltIterate
        {
            Location = location,
            Select = select,
            Params = parameters,
            OnCompletion = onCompletion,
            Body = new XsltSequenceConstructor { Instructions = bodyInstructions }
        };

        // XTSE3130: validate that xsl:next-iteration with-params reference iterate params
        ValidateNextIterationParams(result.Body, paramNames, location);

        return result;
    }


    private XsltChoose ParseChoose(XElement element, SourceLocation? location)
    {
        // XTSE0010: xsl:choose must not contain text content
        foreach (var node in element.Nodes())
        {
            if (node is XText text && !IsXmlWhitespaceOnly(text.Value))
                throw new XsltException("XTSE0010: Text content is not allowed in xsl:choose", location);
        }

        var whens = new List<XsltWhen>();
        XsltSequenceConstructor? otherwise = null;

        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "when")
            {
                if (otherwise != null)
                    throw new XsltException("XTSE0010: xsl:when must not appear after xsl:otherwise in xsl:choose", location);
                var testAttr = child.Attribute("test")
                    ?? throw new XsltException("XTSE0010: xsl:when requires a 'test' attribute", location);
                // Resolve namespace prefixes against the xsl:when itself so that locally-declared
                // xmlns:* on the when (e.g. DocBook xslTNG's `<xsl:when xmlns:ls="..." test="/ls:locale">`)
                // are visible to the test expression.
                var savedNsCtx = _nsContext;
                _nsContext = child;
                XQueryExpression test;
                try { test = ParseExpr(testAttr.Value, testAttr); }
                finally { _nsContext = savedNsCtx; }
                whens.Add(new XsltWhen
                {
                    Test = test,
                    Body = ParseSequenceConstructor(child)
                });
            }
            else if (child.Name == XsltNs + "otherwise")
            {
                if (otherwise != null)
                    throw new XsltException("XTSE0010: xsl:choose must not contain more than one xsl:otherwise", location);
                otherwise = ParseSequenceConstructor(child);
            }
            else
                throw new XsltException($"XTSE0010: Only xsl:when and xsl:otherwise are allowed as children of xsl:choose, found {child.Name.LocalName}",
                    GetSourceLocation(child));
        }

        // XTSE0010: xsl:choose must contain at least one xsl:when
        if (whens.Count == 0)
            throw new XsltException("XTSE0010: xsl:choose must contain at least one xsl:when element", location);

        return new XsltChoose
        {
            Location = location,
            When = whens,
            Otherwise = otherwise
        };
    }


    /// <summary>
    /// Parses xsl:switch (XSLT 4.0) — like xsl:choose but with a select expression.
    /// </summary>
    private XsltSwitch ParseSwitch(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select")
            ?? throw new XsltException("XTSE0010: xsl:switch requires a 'select' attribute", location);

        var whens = new List<Ast.XsltWhen>();
        Ast.XsltSequenceConstructor? otherwise = null;

        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "when")
            {
                if (otherwise != null)
                    throw new XsltException("XTSE0010: xsl:when must not appear after xsl:otherwise in xsl:switch", location);
                var testAttr = child.Attribute("test")
                    ?? throw new XsltException("XTSE0010: xsl:when requires a 'test' attribute", location);
                whens.Add(new Ast.XsltWhen
                {
                    Test = ParseExpr(testAttr.Value, testAttr),
                    Body = ParseBranchBody(child)
                });
            }
            else if (child.Name == XsltNs + "otherwise")
            {
                if (otherwise != null)
                    throw new XsltException("XTSE0010: xsl:switch must not contain more than one xsl:otherwise", location);
                otherwise = ParseBranchBody(child);
            }
            else
                throw new XsltException($"XTSE0010: Only xsl:when and xsl:otherwise are allowed as children of xsl:switch",
                    GetSourceLocation(child));
        }

        if (whens.Count == 0)
            throw new XsltException("XTSE0010: xsl:switch must contain at least one xsl:when element", location);

        return new Ast.XsltSwitch
        {
            Location = location,
            Select = ParseExpr(selectAttr.Value, selectAttr),
            When = whens,
            Otherwise = otherwise
        };
    }


    /// <summary>
    /// Parses xsl:for-each-member (XSLT 4.0) — iterates over array members.
    /// </summary>    /// <summary>
    /// Parses xsl:for-each-member (XSLT 4.0) — iterates over array members.
    /// </summary>
    private Ast.XsltForEachMember ParseForEachMember(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select")
            ?? throw new XsltException("XTSE0010: xsl:for-each-member requires a 'select' attribute", location);

        return new Ast.XsltForEachMember
        {
            Location = location,
            Select = ParseExpr(selectAttr.Value, selectAttr),
            Body = ParseSequenceConstructor(element)
        };
    }


    private XsltTry ParseTry(XElement element, SourceLocation? location)
    {
        var rollbackAttr = element.Attribute("rollback-output");
        var selectAttr = element.Attribute("select");

        var catches = new List<XsltCatch>();
        var catchElements = new List<XElement>();
        XsltSequenceConstructor? bodyContent = null;
        var expandText = IsExpandTextActive(element);

        // XTSE3140: xsl:try with select must not have child content other than xsl:catch and xsl:fallback
        if (selectAttr != null)
        {
            foreach (var node in element.Nodes())
            {
                if (node is XElement child && child.Name != XsltNs + "catch" && child.Name != XsltNs + "fallback")
                    throw new XsltException("XTSE3140: xsl:try with a select attribute must not have content other than xsl:catch and xsl:fallback",
                        GetSourceLocation(child));
                if (node is XText text && !IsXmlWhitespaceOnly(text.Value))
                    throw new XsltException("XTSE3140: xsl:try with a select attribute must not have text content", location);
            }
        }

        // Collect variable names declared in the try body (for scope validation)
        var tryBodyVarNames = new HashSet<string>();

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child when child.Name == XsltNs + "catch":
                {
                    var errorsAttr = child.Attribute("errors");
                    var errors = new List<QName>();
                    if (errorsAttr != null)
                    {
                        foreach (var e in errorsAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var trimmed = e.Trim();
                            if (trimmed == "*")
                            {
                                // Wildcard: matches any error code
                                errors.Add(new QName(NamespaceId.None, "*"));
                            }
                            else if (trimmed.StartsWith("*:", StringComparison.Ordinal))
                            {
                                // *:localname — matches localname in any namespace
                                errors.Add(new QName(NamespaceId.None, trimmed[2..], "*"));
                            }
                            else
                            {
                                errors.Add(ParseQName(trimmed, child));
                            }
                        }
                    }

                    // xsl:catch can have either a select attribute or a body
                    var catchSelectAttr = child.Attribute("select");

                    // XTSE3150: xsl:catch with select must not have content
                    ValidateSelectContentExclusive(catchSelectAttr, child, "XTSE3150", "xsl:catch", GetSourceLocation(child));

                    catches.Add(new XsltCatch
                    {
                        Errors = errors,
                        SelectExpression = catchSelectAttr != null ? ParseExpr(catchSelectAttr.Value, catchSelectAttr) : null,
                        Body = catchSelectAttr == null ? ParseSequenceConstructor(child) : null
                    });
                    catchElements.Add(child);
                    break;
                }
                case XElement child:
                    bodyContent ??= new XsltSequenceConstructor { Instructions = new List<XsltInstruction>() };
                    ((List<XsltInstruction>)bodyContent.Instructions).Add(ParseInstruction(child));
                    // Track variable declarations in try body
                    if (child.Name == XsltNs + "variable")
                    {
                        var nameAttr = child.Attribute("name");
                        if (nameAttr != null)
                            tryBodyVarNames.Add(nameAttr.Value);
                    }
                    break;
                case XText text:
                    if (!IsXmlWhitespaceOnly(text.Value))
                    {
                        bodyContent ??= new XsltSequenceConstructor { Instructions = new List<XsltInstruction>() };
                        ((List<XsltInstruction>)bodyContent.Instructions).Add(CreateTextInstruction(text.Value, expandText, element));
                    }
                    break;
            }
        }

        // XPST0008: Variables declared in xsl:try body are not visible in xsl:catch,
        // but only flag this if the variable doesn't also exist in an outer scope
        if (tryBodyVarNames.Count > 0)
        {
            // Remove names that are also visible from outer scope
            foreach (var varName in tryBodyVarNames.ToList())
            {
                if (IsVariableDeclaredInOuterScope(element, varName))
                    tryBodyVarNames.Remove(varName);
            }

            foreach (var catchElem in catchElements)
            {
                var catchXml = catchElem.ToString();
                foreach (var varName in tryBodyVarNames)
                {
                    if (catchXml.Contains($"${varName}", StringComparison.Ordinal))
                        throw new XsltException($"XPST0008: Variable ${varName} declared in xsl:try is not visible in xsl:catch", location);
                }
            }
        }

        return new XsltTry
        {
            Location = location,
            SelectExpression = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Body = selectAttr == null ? (bodyContent ?? new XsltSequenceConstructor { Instructions = [] }) : null,
            Catches = catches,
            Rollback = rollbackAttr?.Value != "no"
        };
    }


    private XsltSequence ParseSequenceInstr(XElement element, SourceLocation? location)
    {
        // XTSE0090: 'as' attribute is not permitted on xsl:sequence
        if (element.Attribute("as") != null)
            throw new XsltException("XTSE0090: Attribute 'as' is not permitted on xsl:sequence", location);

        var selectAttr = element.Attribute("select");
        var hasContent = element.Nodes().Any(n => (n is XElement e && e.Name != XsltNs + "fallback") || (n is XText t && !string.IsNullOrWhiteSpace(t.Value)));

        // XTSE3185 (XSLT 3.0): xsl:sequence must not have both select and content
        if (selectAttr != null && hasContent)
            throw new XsltException("XTSE3185: xsl:sequence must not have both a select attribute and child content", location);

        // Content: parse when no select and element has content (child elements OR significant text).
        // Text-only content is important for expand-text TVTs like <xsl:sequence expand-text="yes">{expr}</xsl:sequence>.
        var hasContentForParsing = selectAttr == null && (element.HasElements || hasContent);

        return new XsltSequence
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = hasContentForParsing
                ? ParseSequenceConstructor(element)
                : null
        };
    }


    private XsltBreak ParseBreak(XElement element, SourceLocation? location)
    {
        // Validate lexical enclosure: must be inside xsl:iterate body
        ValidateIterateChildLocation(element, "xsl:break", location);

        // Validate: must be last instruction in its sequence constructor (XTSE3120)
        ValidateLastInSequence(element, "xsl:break", location);

        var selectAttr = element.Attribute("select");

        // Validate: select and content are mutually exclusive (XTSE3125)
        if (selectAttr != null && element.Nodes().Any())
            throw new XsltException("XTSE3125: xsl:break must not have both a select attribute and content", location);

        return new XsltBreak
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.HasElements
                ? ParseSequenceConstructor(element)
                : null
        };
    }


    private XsltFork ParseFork(XElement element, SourceLocation? location)
    {
        var forEachGroups = new List<XsltForEachGroup>();
        var sequences = new List<XsltSequenceConstructor>();
        var resultDocuments = new List<XsltResultDocument>();

        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "for-each-group")
                forEachGroups.Add((XsltForEachGroup)ParseForEachGroup(child, GetSourceLocation(child)));
            else if (child.Name == XsltNs + "sequence")
            {
                // xsl:sequence in xsl:fork: parse as instruction and wrap in a sequence constructor
                var instr = ParseSequenceInstr(child, GetSourceLocation(child));
                sequences.Add(new XsltSequenceConstructor { Instructions = [instr] });
            }
            else if (child.Name == XsltNs + "result-document")
                resultDocuments.Add((XsltResultDocument)ParseResultDocument(child, GetSourceLocation(child)));
        }

        return new XsltFork
        {
            Location = location,
            ForEachGroups = forEachGroups,
            Sequences = sequences,
            ResultDocuments = resultDocuments
        };
    }


    /// <summary>
    /// Validates that xsl:break or xsl:next-iteration is lexically within an xsl:iterate body.
    /// Throws XTSE0010 if not inside iterate, XTSE3120 if inside a forbidden construct.
    /// </summary>
    private static void ValidateIterateChildLocation(XElement element, string instrName, SourceLocation? location)
    {
        var current = element.Parent;
        while (current != null)
        {
            if (current.Name == XsltNs + "iterate")
                return; // Found enclosing xsl:iterate — valid

            if (current.Name == XsltNs + "template" || current.Name == XsltNs + "function")
                throw new XsltException($"XTSE0010: {instrName} must be lexically within xsl:iterate", location);

            // Check for forbidden enclosing constructs
            if (current.Name.Namespace != XsltNs || // LRE
                current.Name.LocalName is "for-each" or "for-each-group" or "analyze-string")
                throw new XsltException($"XTSE3120: {instrName} must not appear within {current.Name.LocalName}", location);

            current = current.Parent;
        }
        throw new XsltException($"XTSE0010: {instrName} must be lexically within xsl:iterate", location);
    }

}
