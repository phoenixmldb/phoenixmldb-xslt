using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Base class for all XSLT instructions.
/// </summary>
public abstract class XsltInstruction
{
    /// <summary>
    /// Source location for error reporting.
    /// </summary>
    public SourceLocation? Location { get; init; }

    /// <summary>
    /// Explicit version attribute on this XSLT element (e.g., "1.0").
    /// When set, overrides the effective XSLT version for this instruction's scope.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Default collation URI from the default-collation attribute on this XSLT element.
    /// When set, overrides the default collation for XPath expressions in this scope.
    /// </summary>
    public string? DefaultCollation { get; set; }

    /// <summary>
    /// Static base URI from xml:base on this instruction element.
    /// When set, overrides the static base URI for expressions in this instruction's scope.
    /// </summary>
#pragma warning disable CA1056 // URI property should be System.Uri - matches existing _staticBaseUriStack string pattern
    public string? StaticBaseUri { get; set; }
#pragma warning restore CA1056

    /// <summary>
    /// Execute this instruction.
    /// </summary>
    public abstract ValueTask ExecuteAsync(XsltExecutionContext context);

    /// <summary>
    /// Accept a visitor for analysis or transformation passes.
    /// </summary>
    public abstract T Accept<T>(IXsltInstructionVisitor<T> visitor);
}

/// <summary>
/// Represents a sequence constructor (content of templates, etc.).
/// </summary>
public sealed class XsltSequenceConstructor : XsltInstruction
{
    public required IReadOnlyList<XsltInstruction> Instructions { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitSequenceConstructor(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        // Check if we have on-empty or on-non-empty instructions
        bool hasOnEmpty = false, hasOnNonEmpty = false;
        for (int i = 0; i < Instructions.Count; i++)
        {
            if (Instructions[i] is XsltOnEmpty) hasOnEmpty = true;
            else if (Instructions[i] is XsltOnNonEmpty) hasOnNonEmpty = true;
        }

        if (!hasOnEmpty && !hasOnNonEmpty)
        {
            // Fast path: no conditional instructions
            foreach (var instruction in Instructions)
            {
                if (instruction.Version != null) context.PushVersion(instruction.Version);
                if (instruction.DefaultCollation != null) context.PushCollation(instruction.DefaultCollation);
                if (instruction.StaticBaseUri != null) context.PushStaticBaseUri(instruction.StaticBaseUri);
                try
                {
                    await instruction.ExecuteAsync(context).ConfigureAwait(false);
                }
                finally
                {
                    if (instruction.StaticBaseUri != null) context.PopStaticBaseUri();
                    if (instruction.DefaultCollation != null) context.PopCollation();
                    if (instruction.Version != null) context.PopVersion();
                }
            }
            return;
        }

        if (!hasOnNonEmpty)
        {
            // Only on-empty (must be at end per spec): efficient single-pass approach
            // Phase 1: execute non-conditional instructions, track content via output snapshot
            context.BeginContentTracking();
            foreach (var instruction in Instructions)
            {
                if (instruction is not XsltOnEmpty)
                {
                    if (instruction.Version != null) context.PushVersion(instruction.Version);
                    if (instruction.DefaultCollation != null) context.PushCollation(instruction.DefaultCollation);
                    if (instruction.StaticBaseUri != null) context.PushStaticBaseUri(instruction.StaticBaseUri);
                    try { await instruction.ExecuteAsync(context).ConfigureAwait(false); }
                    finally { if (instruction.StaticBaseUri != null) context.PopStaticBaseUri(); if (instruction.DefaultCollation != null) context.PopCollation(); if (instruction.Version != null) context.PopVersion(); }
                }
            }
            bool wasPopulated = context.EndContentTracking();

            // Phase 2: execute on-empty only if no content was produced
            if (!wasPopulated)
            {
                foreach (var instruction in Instructions)
                {
                    if (instruction is XsltOnEmpty)
                    {
                        if (instruction.Version != null) context.PushVersion(instruction.Version);
                        if (instruction.DefaultCollation != null) context.PushCollation(instruction.DefaultCollation);
                        if (instruction.StaticBaseUri != null) context.PushStaticBaseUri(instruction.StaticBaseUri);
                        try { await instruction.ExecuteAsync(context).ConfigureAwait(false); }
                        finally { if (instruction.StaticBaseUri != null) context.PopStaticBaseUri(); if (instruction.DefaultCollation != null) context.PopCollation(); if (instruction.Version != null) context.PopVersion(); }
                    }
                }
            }
            return;
        }

        // Has on-non-empty (can appear anywhere in sequence constructor):
        // Two-pass approach — first determine if non-conditional content is produced,
        // then execute everything in order with conditionals resolved.
        // Phase 1: probe — execute non-conditional instructions to determine population
        var savedOutput = context.SaveOutput();
        context.BeginContentTracking();
        foreach (var instruction in Instructions)
        {
            if (instruction is not XsltOnEmpty and not XsltOnNonEmpty)
                await instruction.ExecuteAsync(context).ConfigureAwait(false);
        }
        bool wasPopulated2 = context.EndContentTracking();

        // Discard Phase 1 output and restore pre-Phase-1 state
        context.RestoreOutput(savedOutput);

        // Phase 2: execute all instructions in order with conditionals resolved.
        // When content is empty, suppress empty-string separators during non-conditional
        // instruction re-execution so separator spaces don't pollute the on-empty output.
        if (!wasPopulated2)
            context.SuppressEmptyStringSeparators();
        try
        {
            foreach (var instruction in Instructions)
            {
                if (instruction is XsltOnEmpty)
                {
                    if (!wasPopulated2)
                        await instruction.ExecuteAsync(context).ConfigureAwait(false);
                }
                else if (instruction is XsltOnNonEmpty)
                {
                    if (wasPopulated2)
                        await instruction.ExecuteAsync(context).ConfigureAwait(false);
                }
                else
                {
                    await instruction.ExecuteAsync(context).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (!wasPopulated2)
                context.RestoreEmptyStringSeparators();
        }
    }
}

/// <summary>
/// xsl:apply-templates instruction.
/// </summary>
public sealed class XsltApplyTemplates : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public QName? Mode { get; init; }
    public bool UseCurrentMode { get; init; }
    public List<XsltSort> Sorts { get; init; } = new();
    public List<XsltWithParam> WithParams { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitApplyTemplates(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        var effectiveMode = UseCurrentMode ? context.CurrentMode : Mode;
        await context.ApplyTemplatesAsync(Select, effectiveMode, Sorts, WithParams).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:call-template instruction.
/// </summary>
public sealed class XsltCallTemplate : XsltInstruction
{
    public required QName Name { get; init; }
    public List<XsltWithParam> WithParams { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitCallTemplate(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CallTemplateAsync(Name, WithParams).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:apply-imports instruction.
/// </summary>
public sealed class XsltApplyImports : XsltInstruction
{
    public List<XsltWithParam> WithParams { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitApplyImports(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ApplyImportsAsync(WithParams).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:next-match instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltNextMatch : XsltInstruction
{
    public List<XsltWithParam> WithParams { get; init; } = new();
    public XsltSequenceConstructor? Fallback { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitNextMatch(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.NextMatchAsync(WithParams, Fallback).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:for-each instruction.
/// </summary>
public sealed class XsltForEach : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public List<XsltSort> Sorts { get; init; } = new();
    public required XsltSequenceConstructor Body { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitForEach(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ForEachAsync(Select, Sorts, Body).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:for-each-group instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltForEachGroup : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public XQueryExpression? GroupBy { get; init; }
    public XQueryExpression? GroupAdjacent { get; init; }
    public XsltPattern? GroupStartingWith { get; init; }
    public XsltPattern? GroupEndingWith { get; init; }
    public XsltAttributeValueTemplate? Collation { get; init; }
    /// <summary>
    /// XSLT 3.0: If true, group-by/group-adjacent evaluates to a sequence of values
    /// that are treated as a composite key.
    /// </summary>
    public bool Composite { get; init; }
    public List<XsltSort> Sorts { get; init; } = new();
    public required XsltSequenceConstructor Body { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitForEachGroup(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ForEachGroupAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:iterate instruction (XSLT 3.0).
/// </summary>
public sealed class XsltIterate : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public List<XsltParam> Params { get; init; } = new();
    public XsltSequenceConstructor? OnCompletion { get; init; }
    public required XsltSequenceConstructor Body { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitIterate(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.IterateAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:if instruction.
/// </summary>
public sealed class XsltIf : XsltInstruction
{
    public required XQueryExpression Test { get; init; }
    public required XsltSequenceConstructor Then { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitIf(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        if (await context.EvaluateBooleanAsync(Test).ConfigureAwait(false))
        {
            await Then.ExecuteAsync(context).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// xsl:choose instruction.
/// </summary>
public sealed class XsltChoose : XsltInstruction
{
    public required IReadOnlyList<XsltWhen> When { get; init; }
    public XsltSequenceConstructor? Otherwise { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitChoose(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        foreach (var when in When)
        {
            if (await context.EvaluateBooleanAsync(when.Test).ConfigureAwait(false))
            {
                await when.Body.ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        if (Otherwise != null)
        {
            await Otherwise.ExecuteAsync(context).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// xsl:when clause in xsl:choose.
/// </summary>
public sealed class XsltWhen
{
    public required XQueryExpression Test { get; init; }
    public required XsltSequenceConstructor Body { get; init; }
}

/// <summary>
/// xsl:switch instruction (XSLT 4.0).
/// Like xsl:choose but with a select expression that provides the context for each test.
/// </summary>
public sealed class XsltSwitch : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public required IReadOnlyList<XsltWhen> When { get; init; }
    public XsltSequenceConstructor? Otherwise { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitChoose(
        new XsltChoose { When = When, Otherwise = Otherwise });

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.SwitchAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:for-each-member instruction (XSLT 4.0).
/// Iterates over members of an array.
/// </summary>
public sealed class XsltForEachMember : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public required XsltSequenceConstructor Body { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => default!;

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ForEachMemberAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:record instruction (XSLT 4.0).
/// Constructs a record (map with string keys) from xsl:entry children.
/// </summary>
public sealed class XsltRecord : XsltInstruction
{
    public List<(string Name, XsltSequenceConstructor Value)> Entries { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitRecord(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateRecordAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:try instruction (XSLT 3.0).
/// </summary>
public sealed class XsltTry : XsltInstruction
{
    /// <summary>
    /// The select attribute (XPath expression) - mutually exclusive with Body.
    /// </summary>
    public XQueryExpression? SelectExpression { get; init; }

    /// <summary>
    /// The sequence constructor body - mutually exclusive with SelectExpression.
    /// </summary>
    public XsltSequenceConstructor? Body { get; init; }

    public List<XsltCatch> Catches { get; init; } = new();
    public bool Rollback { get; init; } = true;

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitTry(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.TryAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:catch clause in xsl:try.
/// </summary>
public sealed class XsltCatch
{
    public List<QName> Errors { get; init; } = new();

    /// <summary>
    /// The select attribute (XPath expression) - mutually exclusive with Body.
    /// </summary>
    public XQueryExpression? SelectExpression { get; init; }

    /// <summary>
    /// The sequence constructor body - mutually exclusive with SelectExpression.
    /// </summary>
    public XsltSequenceConstructor? Body { get; init; }
}

/// <summary>
/// xsl:element instruction.
/// </summary>
public sealed class XsltElement : XsltInstruction
{
    public required XsltAttributeValueTemplate Name { get; init; }
    public XsltAttributeValueTemplate? Namespace { get; init; }
    public List<QName> UseAttributeSets { get; init; } = new();
    public bool? InheritNamespaces { get; init; }
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }
    public required XsltSequenceConstructor Content { get; init; }

    /// <summary>
    /// Effective base URI from xml:base on the xsl:element instruction.
    /// When set, affects static-base-uri() for XPath expressions in this scope.
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// In-scope namespace bindings from the stylesheet element (prefix → URI).
    /// Used to resolve prefixed element names when no namespace attribute is specified.
    /// </summary>
    public Dictionary<string, string> InScopeNamespaces { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitElement(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateElementAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:attribute instruction.
/// </summary>
public sealed class XsltAttribute : XsltInstruction
{
    public required XsltAttributeValueTemplate Name { get; init; }
    public XsltAttributeValueTemplate? Namespace { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public XsltAttributeValueTemplate? Separator { get; init; }
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }
    public Dictionary<string, string> InScopeNamespaces { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitAttribute(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateAttributeAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:text instruction.
/// </summary>
public sealed class XsltText : XsltInstruction
{
    public required string Value { get; init; }
    public bool DisableOutputEscaping { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitText(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        // Use WriteTextItem for sequence accumulation support (but WriteText if DOE is set)
        if (DisableOutputEscaping)
            context.WriteText(Value, true);
        else
            context.WriteTextItem(Value);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Literal text node.
/// </summary>
public sealed class XsltLiteralText : XsltInstruction
{
    public required string Value { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitLiteralText(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        context.WriteText(Value, false);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Text value template (TVT) — text with embedded {expr} expressions when expand-text="yes".
/// </summary>
public sealed class XsltTextValueTemplate : XsltInstruction
{
    public required XsltAttributeValueTemplate Template { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitTextValueTemplate(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in Template.Parts)
        {
            sb.Append(await part.EvaluateAsync(context).ConfigureAwait(false));
        }
        // Use WriteTextItem for sequence accumulation support (same as XsltText)
        context.WriteTextItem(sb.ToString());
    }
}

/// <summary>
/// xsl:value-of instruction.
/// </summary>
public sealed class XsltValueOf : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public XsltAttributeValueTemplate? Separator { get; init; }
    public bool DisableOutputEscaping { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitValueOf(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ValueOfAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:copy instruction.
/// </summary>
public sealed class XsltCopy : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public bool? CopyNamespaces { get; init; }
    public bool? InheritNamespaces { get; init; }
    public List<QName> UseAttributeSets { get; init; } = new();
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitCopy(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CopyAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:copy-of instruction.
/// </summary>
public sealed class XsltCopyOf : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public bool? CopyNamespaces { get; init; }
    public bool? CopyAccumulators { get; init; }
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitCopyOf(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CopyOfAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:sequence instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltSequence : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitSequence(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.SequenceAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:comment instruction.
/// </summary>
public sealed class XsltComment : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitComment(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateCommentAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:processing-instruction instruction.
/// </summary>
public sealed class XsltProcessingInstruction : XsltInstruction
{
    public required XsltAttributeValueTemplate Name { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitProcessingInstruction(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreatePIAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:namespace instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltNamespace : XsltInstruction
{
    public required XsltAttributeValueTemplate Name { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitNamespace(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateNamespaceAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:document instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltDocument : XsltInstruction
{
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }
    public required XsltSequenceConstructor Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitDocument(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateDocumentAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:result-document instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltResultDocument : XsltInstruction
{
    public XsltAttributeValueTemplate? Href { get; init; }
    public XsltAttributeValueTemplate? Format { get; init; }
    /// <summary>
    /// The format name resolved at compile time (namespace-aware QName).
    /// Set when the format attribute is a static value (not a dynamic AVT).
    /// </summary>
    public QName? ResolvedFormatName { get; init; }
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }
    public XsltAttributeValueTemplate? Method { get; init; }
    public XsltAttributeValueTemplate? OmitXmlDeclaration { get; init; }
    public XsltAttributeValueTemplate? Encoding { get; init; }
    public XsltAttributeValueTemplate? Indent { get; init; }
    public bool? BuildTree { get; init; }
    public XsltAttributeValueTemplate? ItemSeparator { get; init; }
    public XsltAttributeValueTemplate? AllowDuplicateNames { get; init; }
    public List<QName> UseCharacterMaps { get; init; } = [];
    /// <summary>
    /// Namespace bindings from the source element, for resolving prefixed format names at runtime.
    /// </summary>
    public IReadOnlyDictionary<string, string>? NamespaceBindings { get; init; }
    public required XsltSequenceConstructor Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitResultDocument(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ResultDocumentAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:message instruction.
/// </summary>
public sealed class XsltMessage : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public bool Terminate { get; init; }
    public XsltAttributeValueTemplate? TerminateAvt { get; init; }
    public string? ErrorCode { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitMessage(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.MessageAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:assert instruction (XSLT 3.0).
/// </summary>
public sealed class XsltAssert : XsltInstruction
{
    public required XQueryExpression Test { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public string? ErrorCode { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitAssert(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.AssertAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:variable instruction.
/// </summary>
public sealed class XsltVariableInstruction : XsltInstruction
{
    public required QName Name { get; init; }
    public XdmSequenceType? As { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public Uri? BaseUri { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitVariableInstruction(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.BindVariableAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:param instruction (in template body).
/// </summary>
public sealed class XsltParamInstruction : XsltInstruction
{
    public required QName Name { get; init; }
    public XdmSequenceType? As { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public bool Required { get; init; }
    public bool Tunnel { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitParamInstruction(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.BindParamAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:with-param in apply-templates or call-template.
/// </summary>
public sealed class XsltWithParam
{
    public required QName Name { get; init; }
    public XdmSequenceType? As { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public bool Tunnel { get; init; }
}

/// <summary>
/// xsl:sort specification.
/// </summary>
public sealed class XsltSort
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public XsltAttributeValueTemplate? Lang { get; init; }
    public XsltAttributeValueTemplate? Order { get; init; } // "ascending" or "descending"
    public XsltAttributeValueTemplate? Collation { get; init; }
    public XsltAttributeValueTemplate? Stable { get; init; }
    public XsltAttributeValueTemplate? CaseOrder { get; init; }
    public XsltAttributeValueTemplate? DataType { get; init; }
}

/// <summary>
/// xsl:number instruction.
/// </summary>
public sealed class XsltNumber : XsltInstruction
{
    public XQueryExpression? Value { get; init; }
    public XQueryExpression? Select { get; init; }
    public NumberLevel Level { get; init; } = NumberLevel.Single;
    public XsltPattern? Count { get; init; }
    public XsltPattern? From { get; init; }
    public XsltAttributeValueTemplate? Format { get; init; }
    public XsltAttributeValueTemplate? Lang { get; init; }
    public XsltAttributeValueTemplate? LetterValue { get; init; }
    public XsltAttributeValueTemplate? OrdinalValue { get; init; }
    public XsltAttributeValueTemplate? GroupingSeparator { get; init; }
    public XsltAttributeValueTemplate? GroupingSize { get; init; }
    public XsltAttributeValueTemplate? StartAt { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitNumber(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.NumberAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// Level for xsl:number.
/// </summary>
public enum NumberLevel
{
    Single,
    Multiple,
    Any
}

/// <summary>
/// xsl:perform-sort instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltPerformSort : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public List<XsltSort> Sorts { get; init; } = new();
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitPerformSort(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.PerformSortAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:analyze-string instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltAnalyzeString : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public required XsltAttributeValueTemplate Regex { get; init; }
    public XsltAttributeValueTemplate? Flags { get; init; }
    public XsltSequenceConstructor? MatchingSubstring { get; init; }
    public XsltSequenceConstructor? NonMatchingSubstring { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitAnalyzeString(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.AnalyzeStringAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:break instruction (XSLT 3.0).
/// </summary>
public sealed class XsltBreak : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitBreak(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        context.Break(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// xsl:next-iteration instruction (XSLT 3.0).
/// </summary>
public sealed class XsltNextIteration : XsltInstruction
{
    public List<XsltWithParam> WithParams { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitNextIteration(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        context.NextIteration(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// xsl:fork instruction (XSLT 3.0).
/// </summary>
public sealed class XsltFork : XsltInstruction
{
    public List<XsltForEachGroup> ForEachGroups { get; init; } = new();
    public List<XsltSequenceConstructor> Sequences { get; init; } = new();
    public List<XsltResultDocument> ResultDocuments { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitFork(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ForkAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:merge instruction (XSLT 3.0).
/// Merges multiple pre-sorted input sequences.
/// </summary>
public sealed class XsltMerge : XsltInstruction
{
    public List<XsltMergeSource> Sources { get; init; } = new();
    public required XsltSequenceConstructor Action { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitMerge(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.MergeAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:merge-source child of xsl:merge.
/// </summary>
public sealed class XsltMergeSource
{
    public string? Name { get; init; }
    public XQueryExpression? ForEachItem { get; init; }
    public XQueryExpression? ForEachSource { get; init; }
    public required XQueryExpression Select { get; init; }
    public bool SortBeforeMerge { get; init; }
    public List<XsltMergeKey> MergeKeys { get; init; } = new();
    public List<QName> UseAccumulators { get; init; } = new();
    public SourceLocation? Location { get; init; }
}

/// <summary>
/// xsl:merge-key child of xsl:merge-source.
/// </summary>
public sealed class XsltMergeKey
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public XsltAttributeValueTemplate? Order { get; init; }
    public XsltAttributeValueTemplate? Collation { get; init; }
    public XsltAttributeValueTemplate? DataType { get; init; }
    public XsltAttributeValueTemplate? Lang { get; init; }
}

/// <summary>
/// xsl:map instruction (XSLT 3.0).
/// </summary>
public sealed class XsltMap : XsltInstruction
{
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitMap(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateMapAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:map-entry instruction (XSLT 3.0).
/// </summary>
public sealed class XsltMapEntry : XsltInstruction
{
    public required XQueryExpression Key { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitMapEntry(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateMapEntryAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:array instruction (XSLT 3.0).
/// </summary>
public sealed class XsltArray : XsltInstruction
{
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitArray(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateArrayAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// xsl:array-member instruction (XSLT 3.0).
/// </summary>
public sealed class XsltArrayMember : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitArrayMember(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateArrayMemberAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// Literal result element.
/// </summary>
public sealed class XsltLiteralResultElement : XsltInstruction
{
    public required QName Name { get; init; }
    public Dictionary<QName, XsltAttributeValueTemplate> Attributes { get; init; } = new();
    public Dictionary<string, string> NamespaceDeclarations { get; init; } = new();
    public List<QName> UseAttributeSets { get; init; } = new();
    public bool? InheritNamespaces { get; init; }
    public required XsltSequenceConstructor Content { get; init; }

    /// <summary>
    /// Per-element exclude-result-prefixes (from xsl:exclude-result-prefixes attribute on the LRE).
    /// </summary>
    public HashSet<string> ExcludeResultPrefixes { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitLiteralResultElement(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateLiteralElementAsync(this).ConfigureAwait(false);
    }
}

/// <summary>
/// Attribute value template (AVT).
/// </summary>
public class XsltAttributeValueTemplate
{
    public required IReadOnlyList<AvtPart> Parts { get; init; }

    public static XsltAttributeValueTemplate FromString(string value)
    {
        return new XsltAttributeValueTemplate
        {
            Parts = [new AvtLiteral { Value = value }]
        };
    }
}

/// <summary>
/// Part of an AVT.
/// </summary>
public abstract class AvtPart
{
    public abstract ValueTask<string> EvaluateAsync(XsltExecutionContext context);
}

/// <summary>
/// Literal text in AVT.
/// </summary>
public sealed class AvtLiteral : AvtPart
{
    public required string Value { get; init; }

    public override ValueTask<string> EvaluateAsync(XsltExecutionContext context)
    {
        return ValueTask.FromResult(Value);
    }
}

/// <summary>
/// Expression in AVT (between { and }).
/// </summary>
public sealed class AvtExpression : AvtPart
{
    public required XQueryExpression Expression { get; init; }

    public override async ValueTask<string> EvaluateAsync(XsltExecutionContext context)
    {
        var result = await context.EvaluateAsync(Expression).ConfigureAwait(false);
        return StringifyResult(result, context.IsBackwardsCompatibleMode);
    }

    private static string StringifyResult(object? result, bool backwardsCompatible = false)
    {
        return result switch
        {
            null => "",
            XdmNode node => node.StringValue,
            TextNodeItem tni => tni.Value,
            bool b => b ? "true" : "false",
            decimal m => FormatDecimal(m),
            double d => FormatDouble(d),
            float f => FormatFloat(f),
            XsDateTime xdt => xdt.ToString(),
            XsDate xd => xd.ToString(),
            XsTime xt => xt.ToString(),
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dto => FormatDateTimeOffset(dto),
            TimeSpan ts => System.Xml.XmlConvert.ToString(ts),
            // XSLT 2.0 spec §7.6.2: in backwards-compat mode, only the first item is used
            object[] arr when backwardsCompatible => arr.Length > 0 ? StringifyResult(arr[0]) : "",
            object[] arr => StringifySequence(arr),
            IEnumerable<object?> seq when backwardsCompatible => StringifyResult(seq.FirstOrDefault()),
            IEnumerable<object?> seq => StringifySequence(seq.ToArray()),
            _ => result.ToString() ?? ""
        };
    }

    /// <summary>
    /// Implements XSLT 3.0 §5.7.2 simple content construction for sequences:
    /// 1. Merge adjacent text nodes (TextNodeItem)
    /// 2. Remove zero-length text nodes
    /// 3. Join remaining items with space separator
    /// </summary>
    private static string StringifySequence(object?[] items)
    {
        // Fast path: no TextNodeItems → simple space join
        bool hasTextNodeItems = false;
        foreach (var item in items)
        {
            if (item is TextNodeItem)
            {
                hasTextNodeItems = true;
                break;
            }
        }

        if (!hasTextNodeItems)
            return string.Join(" ", items.Select(x => StringifyResult(x)));

        // §5.7.2: merge adjacent TextNodeItems, remove zero-length ones, join with space
        var processed = new List<string>();
        System.Text.StringBuilder? textRun = null;

        foreach (var item in items)
        {
            if (item is TextNodeItem tni)
            {
                textRun ??= new System.Text.StringBuilder();
                textRun.Append(tni.Value);
            }
            else
            {
                if (textRun != null)
                {
                    // Flush merged text node, skip if zero-length
                    if (textRun.Length > 0)
                        processed.Add(textRun.ToString());
                    textRun = null;
                }
                processed.Add(StringifyResult(item));
            }
        }

        if (textRun != null && textRun.Length > 0)
            processed.Add(textRun.ToString());

        return string.Join(" ", processed);
    }

    private static string FormatDateTimeOffset(DateTimeOffset dto)
    {
        if (dto.Offset == TimeSpan.Zero)
            return dto.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
        return dto.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private static string FormatDecimal(decimal m)
    {
        // XPath canonical decimal format: no trailing zeros
        var s = m.ToString("G", CultureInfo.InvariantCulture);
        if (s.Contains('.', StringComparison.Ordinal))
        {
            s = s.TrimEnd('0').TrimEnd('.');
        }
        return s == "-0" ? "0" : s;
    }

    private static string FormatDouble(double d) => XQuery.Functions.ConcatFunction.FormatDoubleXPath(d);

    private static string FormatFloat(float f) => XQuery.Functions.ConcatFunction.FormatFloatXPath(f);
}

/// <summary>
/// Placeholder for XSLT execution context.
/// </summary>
public abstract class XsltExecutionContext
{
    /// <summary>Gets the current template mode for mode="#current" resolution.</summary>
    public abstract QName? CurrentMode { get; }

    /// <summary>Whether the current context is in XSLT 1.0 backwards-compatible mode.</summary>
    public abstract bool IsBackwardsCompatibleMode { get; }

    /// <summary>Push an effective version override onto the version stack.</summary>
    public virtual void PushVersion(string version) { }

    /// <summary>Pop the most recent version override from the version stack.</summary>
    public virtual void PopVersion() { }

    /// <summary>Push a default collation override onto the collation stack.</summary>
    public virtual void PushCollation(string collation) { }

    /// <summary>Pop the most recent collation override from the collation stack.</summary>
    public virtual void PopCollation() { }

    /// <summary>Push a static base URI override from xml:base on an instruction.</summary>
#pragma warning disable CA1054 // URI parameter should be System.Uri - matches existing _staticBaseUriStack string pattern
    public virtual void PushStaticBaseUri(string baseUri) { }
#pragma warning restore CA1054

    /// <summary>Pop the most recent static base URI override.</summary>
    public virtual void PopStaticBaseUri() { }

    /// <summary>Gets the current default collation URI, or null for codepoint.</summary>
    public virtual string? DefaultCollation => null;

    public abstract ValueTask ApplyTemplatesAsync(XQueryExpression? select, QName? mode,
        List<XsltSort> sorts, List<XsltWithParam> withParams);
    public abstract ValueTask CallTemplateAsync(QName name, List<XsltWithParam> withParams);
    public abstract ValueTask ApplyImportsAsync(List<XsltWithParam> withParams);
    public abstract ValueTask NextMatchAsync(List<XsltWithParam> withParams, XsltSequenceConstructor? fallback);
    public abstract ValueTask ForEachAsync(XQueryExpression select, List<XsltSort> sorts, XsltSequenceConstructor body);
    public abstract ValueTask ForEachGroupAsync(XsltForEachGroup instruction);
    public abstract ValueTask IterateAsync(XsltIterate instruction);
    public abstract ValueTask TryAsync(XsltTry instruction);
    /// <summary>Execute xsl:switch (XSLT 4.0) — evaluates select, pushes as context, then tests when clauses.</summary>
    public abstract ValueTask SwitchAsync(XsltSwitch instruction);
    /// <summary>Execute xsl:for-each-member (XSLT 4.0) — iterates over array members.</summary>
    public abstract ValueTask ForEachMemberAsync(XsltForEachMember instruction);
    public abstract ValueTask<bool> EvaluateBooleanAsync(XQueryExpression expr);
    public abstract ValueTask<object?> EvaluateAsync(XQueryExpression expr);
    public abstract ValueTask CreateElementAsync(XsltElement instruction);
    public abstract ValueTask CreateAttributeAsync(XsltAttribute instruction);
    public abstract void WriteText(string value, bool disableOutputEscaping);
    /// <summary>
    /// Writes a text item. If sequence accumulation is active, adds to the sequence as a separate item.
    /// Otherwise, writes to output (same as WriteText).
    /// </summary>
    public abstract void WriteTextItem(string value);
    public abstract ValueTask ValueOfAsync(XsltValueOf instruction);
    public abstract ValueTask CopyAsync(XsltCopy instruction);
    public abstract ValueTask CopyOfAsync(XsltCopyOf instruction);
    public abstract ValueTask SequenceAsync(XsltSequence instruction);
    public abstract ValueTask CreateCommentAsync(XsltComment instruction);
    public abstract ValueTask CreatePIAsync(XsltProcessingInstruction instruction);
    public abstract ValueTask CreateNamespaceAsync(XsltNamespace instruction);
    public abstract ValueTask CreateDocumentAsync(XsltDocument instruction);
    public abstract ValueTask ResultDocumentAsync(XsltResultDocument instruction);
    public abstract ValueTask MessageAsync(XsltMessage instruction);
    public abstract ValueTask AssertAsync(XsltAssert instruction);
    public abstract ValueTask BindVariableAsync(XsltVariableInstruction instruction);
    public abstract ValueTask BindParamAsync(XsltParamInstruction instruction);
    public abstract ValueTask NumberAsync(XsltNumber instruction);
    public abstract ValueTask PerformSortAsync(XsltPerformSort instruction);
    public abstract ValueTask AnalyzeStringAsync(XsltAnalyzeString instruction);
    public abstract void Break(XsltBreak instruction);
    public abstract void NextIteration(XsltNextIteration instruction);
    public abstract ValueTask ForkAsync(XsltFork instruction);
    public abstract ValueTask MergeAsync(XsltMerge instruction);
    public abstract ValueTask CreateMapAsync(XsltMap instruction);
    public abstract ValueTask CreateMapEntryAsync(XsltMapEntry instruction);
    public abstract ValueTask CreateArrayAsync(XsltArray instruction);
    public abstract ValueTask CreateArrayMemberAsync(XsltArrayMember instruction);
    /// <summary>Execute xsl:record (XSLT 4.0) — constructs a map with string keys from xsl:entry children.</summary>
    public abstract ValueTask CreateRecordAsync(XsltRecord instruction);
    public abstract ValueTask CreateLiteralElementAsync(XsltLiteralResultElement instruction);
    public abstract ValueTask WherePopulatedAsync(XsltWherePopulated instruction);
    public abstract ValueTask OnEmptyAsync(XsltOnEmpty instruction);
    public abstract ValueTask OnNonEmptyAsync(XsltOnNonEmpty instruction);
    public abstract ValueTask EvaluateInstructionAsync(XsltEvaluate instruction);
    public abstract ValueTask SourceDocumentAsync(XsltSourceDocument instruction);

    /// <summary>Begin tracking whether content is produced (for xsl:on-empty/xsl:on-non-empty).</summary>
    public virtual void BeginContentTracking() { }
    /// <summary>End tracking and return whether content was produced.</summary>
    public virtual bool EndContentTracking() => false;
    /// <summary>Save current output state for later restoration (returns opaque state object).</summary>
    public virtual object SaveOutput() => "";
    /// <summary>Restore output to a previously saved state, discarding any output since then.</summary>
    public virtual void RestoreOutput(object savedState) { }
    /// <summary>Suppress separator emission for empty strings (for on-empty content evaluation).</summary>
    public virtual void SuppressEmptyStringSeparators() { }
    /// <summary>Restore normal separator emission.</summary>
    public virtual void RestoreEmptyStringSeparators() { }
}

/// <summary>
/// A no-op instruction (used for xsl:fallback inside supported instructions).
/// </summary>
public sealed class XsltNoOp : XsltInstruction
{
    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitNoOp(this);
    public override ValueTask ExecuteAsync(XsltExecutionContext context) => ValueTask.CompletedTask;
}

/// <summary>
/// Raises a dynamic error when executed (used for extension elements without xsl:fallback).
/// </summary>
public sealed class XsltDynamicError : XsltInstruction
{
    public required string ErrorCode { get; init; }
    public required string Message { get; init; }
    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => default!;
    public override ValueTask ExecuteAsync(XsltExecutionContext context)
        => throw new PhoenixmlDb.Xslt.Engine.XsltException($"{ErrorCode}: {Message}");
}

/// <summary>
/// xsl:where-populated - executes content and includes result only if non-empty.
/// </summary>
public sealed class XsltWherePopulated : XsltInstruction
{
    public required XsltSequenceConstructor Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitWherePopulated(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
        => context.WherePopulatedAsync(this);
}

/// <summary>
/// xsl:on-empty - provides fallback content when parent produces no output.
/// </summary>
public sealed class XsltOnEmpty : XsltInstruction
{
    public XsltSequenceConstructor? Content { get; init; }
    public XQueryExpression? Select { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitOnEmpty(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
        => context.OnEmptyAsync(this);
}

/// <summary>
/// xsl:on-non-empty - includes content only when parent produces non-empty output.
/// </summary>
public sealed class XsltOnNonEmpty : XsltInstruction
{
    public XsltSequenceConstructor? Content { get; init; }
    public XQueryExpression? Select { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitOnNonEmpty(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
        => context.OnNonEmptyAsync(this);
}

/// <summary>
/// xsl:evaluate - dynamically evaluates an XPath expression (XSLT 3.0).
/// </summary>
public sealed class XsltEvaluate : XsltInstruction
{
    public required XQueryExpression Xpath { get; init; }
    public XQueryExpression? ContextItem { get; init; }
    public XsltAttributeValueTemplate? BaseUri { get; init; }
    public XQueryExpression? NamespaceContext { get; init; }
    public XQueryExpression? WithParamsExpr { get; init; }
    public XdmSequenceType? As { get; init; }
    public string? EvaluateDefaultCollation { get; init; }
    public List<XsltWithParam> WithParams { get; init; } = [];
    public XsltSequenceConstructor? Fallback { get; init; }
    /// <summary>
    /// Default in-scope namespace bindings (prefix → URI) from the xsl:evaluate element.
    /// Used when namespace-context is not specified.
    /// </summary>
    public Dictionary<string, string> DefaultNamespaceBindings { get; init; } = new();
    /// <summary>
    /// The xpath-default-namespace from the xsl:evaluate element.
    /// </summary>
    public string? XpathDefaultNamespace { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitEvaluate(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.EvaluateInstructionAsync(this).ConfigureAwait(false);
    }
}
