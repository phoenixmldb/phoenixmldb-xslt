// Visitor methods receive non-null values from Accept<T> dispatch — suppress parameter validation noise
#pragma warning disable CA1062

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Visitor interface for XSLT instruction trees.
/// </summary>
public interface IXsltInstructionVisitor<T>
{
    // Sequence
    T VisitSequenceConstructor(XsltSequenceConstructor insn);

    // Template control
    T VisitApplyTemplates(XsltApplyTemplates insn);
    T VisitCallTemplate(XsltCallTemplate insn);
    T VisitApplyImports(XsltApplyImports insn);
    T VisitNextMatch(XsltNextMatch insn);

    // Iteration
    T VisitForEach(XsltForEach insn);
    T VisitForEachGroup(XsltForEachGroup insn);
    T VisitForEachMember(XsltForEachMember insn);
    T VisitIterate(XsltIterate insn);
    T VisitBreak(XsltBreak insn);
    T VisitNextIteration(XsltNextIteration insn);

    // Conditionals
    T VisitIf(XsltIf insn);
    T VisitChoose(XsltChoose insn);
    T VisitSwitch(XsltSwitch insn);
    T VisitTry(XsltTry insn);

    // Construction
    T VisitElement(XsltElement insn);
    T VisitAttribute(XsltAttribute insn);
    T VisitText(XsltText insn);
    T VisitLiteralText(XsltLiteralText insn);
    T VisitTextValueTemplate(XsltTextValueTemplate insn);
    T VisitValueOf(XsltValueOf insn);
    T VisitCopy(XsltCopy insn);
    T VisitCopyOf(XsltCopyOf insn);
    T VisitSequence(XsltSequence insn);
    T VisitComment(XsltComment insn);
    T VisitProcessingInstruction(XsltProcessingInstruction insn);
    T VisitNamespace(XsltNamespace insn);
    T VisitDocument(XsltDocument insn);
    T VisitLiteralResultElement(XsltLiteralResultElement insn);

    // Output
    T VisitResultDocument(XsltResultDocument insn);
    T VisitMessage(XsltMessage insn);

    // Variables/params
    T VisitVariableInstruction(XsltVariableInstruction insn);
    T VisitParamInstruction(XsltParamInstruction insn);

    // Formatting
    T VisitNumber(XsltNumber insn);
    T VisitPerformSort(XsltPerformSort insn);
    T VisitAnalyzeString(XsltAnalyzeString insn);

    // Assertions
    T VisitAssert(XsltAssert insn);

    // Complex data (3.0)
    T VisitMap(XsltMap insn);
    T VisitMapEntry(XsltMapEntry insn);
    T VisitArray(XsltArray insn);
    T VisitArrayMember(XsltArrayMember insn);

    // Merge (3.0)
    T VisitMerge(XsltMerge insn);

    // Record (4.0)
    T VisitRecord(XsltRecord insn);

    // Advanced control
    T VisitFork(XsltFork insn);
    T VisitWherePopulated(XsltWherePopulated insn);
    T VisitOnEmpty(XsltOnEmpty insn);
    T VisitOnNonEmpty(XsltOnNonEmpty insn);
    T VisitEvaluate(XsltEvaluate insn);

    // Streaming
    T VisitSourceDocument(XsltSourceDocument insn);

    // Error
    T VisitDynamicError(XsltDynamicError insn);

    // Special
    T VisitNoOp(XsltNoOp insn);
}

/// <summary>
/// Base visitor with default traversal behavior.
/// Override methods to customize behavior.
/// </summary>
public abstract class XsltInstructionVisitor<T> : IXsltInstructionVisitor<T>
{
    protected virtual T DefaultVisit(XsltInstruction insn) => default!;

    // Sequence
    public virtual T VisitSequenceConstructor(XsltSequenceConstructor insn) => DefaultVisit(insn);

    // Template control
    public virtual T VisitApplyTemplates(XsltApplyTemplates insn) => DefaultVisit(insn);
    public virtual T VisitCallTemplate(XsltCallTemplate insn) => DefaultVisit(insn);
    public virtual T VisitApplyImports(XsltApplyImports insn) => DefaultVisit(insn);
    public virtual T VisitNextMatch(XsltNextMatch insn) => DefaultVisit(insn);

    // Iteration
    public virtual T VisitForEach(XsltForEach insn) => DefaultVisit(insn);
    public virtual T VisitForEachGroup(XsltForEachGroup insn) => DefaultVisit(insn);
    public virtual T VisitForEachMember(XsltForEachMember insn) => DefaultVisit(insn);
    public virtual T VisitIterate(XsltIterate insn) => DefaultVisit(insn);
    public virtual T VisitBreak(XsltBreak insn) => DefaultVisit(insn);
    public virtual T VisitNextIteration(XsltNextIteration insn) => DefaultVisit(insn);

    // Conditionals
    public virtual T VisitIf(XsltIf insn) => DefaultVisit(insn);
    public virtual T VisitChoose(XsltChoose insn) => DefaultVisit(insn);
    public virtual T VisitSwitch(XsltSwitch insn) => DefaultVisit(insn);
    public virtual T VisitTry(XsltTry insn) => DefaultVisit(insn);

    // Construction
    public virtual T VisitElement(XsltElement insn) => DefaultVisit(insn);
    public virtual T VisitAttribute(XsltAttribute insn) => DefaultVisit(insn);
    public virtual T VisitText(XsltText insn) => DefaultVisit(insn);
    public virtual T VisitLiteralText(XsltLiteralText insn) => DefaultVisit(insn);
    public virtual T VisitTextValueTemplate(XsltTextValueTemplate insn) => DefaultVisit(insn);
    public virtual T VisitValueOf(XsltValueOf insn) => DefaultVisit(insn);
    public virtual T VisitCopy(XsltCopy insn) => DefaultVisit(insn);
    public virtual T VisitCopyOf(XsltCopyOf insn) => DefaultVisit(insn);
    public virtual T VisitSequence(XsltSequence insn) => DefaultVisit(insn);
    public virtual T VisitComment(XsltComment insn) => DefaultVisit(insn);
    public virtual T VisitProcessingInstruction(XsltProcessingInstruction insn) => DefaultVisit(insn);
    public virtual T VisitNamespace(XsltNamespace insn) => DefaultVisit(insn);
    public virtual T VisitDocument(XsltDocument insn) => DefaultVisit(insn);
    public virtual T VisitLiteralResultElement(XsltLiteralResultElement insn) => DefaultVisit(insn);

    // Output
    public virtual T VisitResultDocument(XsltResultDocument insn) => DefaultVisit(insn);
    public virtual T VisitMessage(XsltMessage insn) => DefaultVisit(insn);

    // Variables/params
    public virtual T VisitVariableInstruction(XsltVariableInstruction insn) => DefaultVisit(insn);
    public virtual T VisitParamInstruction(XsltParamInstruction insn) => DefaultVisit(insn);

    // Formatting
    public virtual T VisitNumber(XsltNumber insn) => DefaultVisit(insn);
    public virtual T VisitPerformSort(XsltPerformSort insn) => DefaultVisit(insn);
    public virtual T VisitAnalyzeString(XsltAnalyzeString insn) => DefaultVisit(insn);

    // Assertions
    public virtual T VisitAssert(XsltAssert insn) => DefaultVisit(insn);

    // Complex data (3.0)
    public virtual T VisitMap(XsltMap insn) => DefaultVisit(insn);
    public virtual T VisitMapEntry(XsltMapEntry insn) => DefaultVisit(insn);
    public virtual T VisitArray(XsltArray insn) => DefaultVisit(insn);
    public virtual T VisitArrayMember(XsltArrayMember insn) => DefaultVisit(insn);

    // Merge (3.0)
    public virtual T VisitMerge(XsltMerge insn) => DefaultVisit(insn);

    // Record (4.0)
    public virtual T VisitRecord(XsltRecord insn) => DefaultVisit(insn);

    // Advanced control
    public virtual T VisitFork(XsltFork insn) => DefaultVisit(insn);
    public virtual T VisitWherePopulated(XsltWherePopulated insn) => DefaultVisit(insn);
    public virtual T VisitOnEmpty(XsltOnEmpty insn) => DefaultVisit(insn);
    public virtual T VisitOnNonEmpty(XsltOnNonEmpty insn) => DefaultVisit(insn);
    public virtual T VisitEvaluate(XsltEvaluate insn) => DefaultVisit(insn);

    // Streaming
    public virtual T VisitSourceDocument(XsltSourceDocument insn) => DefaultVisit(insn);

    // Error
    public virtual T VisitDynamicError(XsltDynamicError insn) => DefaultVisit(insn);

    // Special
    public virtual T VisitNoOp(XsltNoOp insn) => DefaultVisit(insn);
}

/// <summary>
/// Visitor that walks the instruction tree without modifying it.
/// Useful for analysis passes. Override methods to add behavior,
/// and call base to continue walking children.
/// </summary>
public abstract class XsltInstructionWalker : XsltInstructionVisitor<object?>
{
    protected override object? DefaultVisit(XsltInstruction insn) => null;

    public virtual void Walk(XsltInstruction insn) => insn.Accept(this);

    public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
    {
        foreach (var instruction in insn.Instructions)
            Walk(instruction);
        return null;
    }

    public override object? VisitApplyTemplates(XsltApplyTemplates insn) => null;
    public override object? VisitCallTemplate(XsltCallTemplate insn) => null;
    public override object? VisitApplyImports(XsltApplyImports insn) => null;

    public override object? VisitNextMatch(XsltNextMatch insn)
    {
        if (insn.Fallback != null) Walk(insn.Fallback);
        return null;
    }

    public override object? VisitForEach(XsltForEach insn)
    {
        Walk(insn.Body);
        return null;
    }

    public override object? VisitForEachGroup(XsltForEachGroup insn)
    {
        Walk(insn.Body);
        return null;
    }

    public override object? VisitForEachMember(XsltForEachMember insn)
    {
        Walk(insn.Body);
        return null;
    }

    public override object? VisitIterate(XsltIterate insn)
    {
        if (insn.OnCompletion != null) Walk(insn.OnCompletion);
        Walk(insn.Body);
        return null;
    }

    public override object? VisitBreak(XsltBreak insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitNextIteration(XsltNextIteration insn) => null;

    public override object? VisitIf(XsltIf insn)
    {
        Walk(insn.Then);
        return null;
    }

    public override object? VisitChoose(XsltChoose insn)
    {
        foreach (var when in insn.When)
            Walk(when.Body);
        if (insn.Otherwise != null) Walk(insn.Otherwise);
        return null;
    }

    public override object? VisitSwitch(XsltSwitch insn)
    {
        foreach (var when in insn.When)
            Walk(when.Body);
        if (insn.Otherwise != null) Walk(insn.Otherwise);
        return null;
    }

    public override object? VisitTry(XsltTry insn)
    {
        if (insn.Body != null) Walk(insn.Body);
        foreach (var c in insn.Catches)
        {
            if (c.Body != null) Walk(c.Body);
        }
        return null;
    }

    public override object? VisitElement(XsltElement insn)
    {
        Walk(insn.Content);
        return null;
    }

    public override object? VisitAttribute(XsltAttribute insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitText(XsltText insn) => null;
    public override object? VisitLiteralText(XsltLiteralText insn) => null;
    public override object? VisitTextValueTemplate(XsltTextValueTemplate insn) => null;

    public override object? VisitValueOf(XsltValueOf insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitCopy(XsltCopy insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitCopyOf(XsltCopyOf insn) => null;

    public override object? VisitSequence(XsltSequence insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitComment(XsltComment insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitProcessingInstruction(XsltProcessingInstruction insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitNamespace(XsltNamespace insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitDocument(XsltDocument insn)
    {
        Walk(insn.Content);
        return null;
    }

    public override object? VisitLiteralResultElement(XsltLiteralResultElement insn)
    {
        Walk(insn.Content);
        return null;
    }

    public override object? VisitResultDocument(XsltResultDocument insn)
    {
        Walk(insn.Content);
        return null;
    }

    public override object? VisitMessage(XsltMessage insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitVariableInstruction(XsltVariableInstruction insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitParamInstruction(XsltParamInstruction insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitNumber(XsltNumber insn) => null;
    public override object? VisitPerformSort(XsltPerformSort insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitAnalyzeString(XsltAnalyzeString insn)
    {
        if (insn.MatchingSubstring != null) Walk(insn.MatchingSubstring);
        if (insn.NonMatchingSubstring != null) Walk(insn.NonMatchingSubstring);
        return null;
    }

    public override object? VisitAssert(XsltAssert insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitMap(XsltMap insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitMapEntry(XsltMapEntry insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitArray(XsltArray insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitArrayMember(XsltArrayMember insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitMerge(XsltMerge insn)
    {
        Walk(insn.Action);
        return null;
    }

    public override object? VisitFork(XsltFork insn)
    {
        foreach (var group in insn.ForEachGroups)
            Walk(group);
        foreach (var seq in insn.Sequences)
            Walk(seq);
        foreach (var rd in insn.ResultDocuments)
            Walk(rd);
        return null;
    }

    public override object? VisitWherePopulated(XsltWherePopulated insn)
    {
        Walk(insn.Content);
        return null;
    }

    public override object? VisitOnEmpty(XsltOnEmpty insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitOnNonEmpty(XsltOnNonEmpty insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitEvaluate(XsltEvaluate insn)
    {
        if (insn.Fallback != null) Walk(insn.Fallback);
        return null;
    }

    public override object? VisitSourceDocument(XsltSourceDocument insn)
    {
        if (insn.Content != null) Walk(insn.Content);
        return null;
    }

    public override object? VisitDynamicError(XsltDynamicError insn) => null;

    public override object? VisitNoOp(XsltNoOp insn) => null;
}
