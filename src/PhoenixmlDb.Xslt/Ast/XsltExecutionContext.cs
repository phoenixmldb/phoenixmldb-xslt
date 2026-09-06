using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

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

    /// <summary>
    /// Push the source location of the currently-executing instruction so any error
    /// raised during its evaluation auto-attaches the right (module, line, column).
    /// Default no-op for contexts that don't track location.
    /// </summary>
    public virtual void PushInstructionLocation(SourceLocation location) { }

    /// <summary>Pop the most recent instruction location.</summary>
    public virtual void PopInstructionLocation() { }

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
