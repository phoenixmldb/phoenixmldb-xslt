using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine.Streamability;

/// <summary>
/// The rules of XSLT 3.0 §19.8.3 and §19.8.4 for sequence constructors and the instructions
/// that follow the general streamability rules (§19.8.1), and the record of whether a verdict
/// depends on a construct the classifier does not model.
/// </summary>
public static partial class StreamabilityClassifier
{
    [ThreadStatic]
    private static bool t_doubt;

    /// <summary>
    /// Classifies with <paramref name="classify"/> and says whether the answer comes only from
    /// rules of the specification that the classifier models. A construct it does not model
    /// (an expression, function or instruction it has no rule for), or a rule it applies
    /// with less information than the specification uses (a static type it does not infer),
    /// makes the answer uncertain: "not streamable" may then be wrong.
    /// </summary>
    internal static PostureSweep ClassifyWithCertainty(Func<PostureSweep> classify, out bool certain)
    {
        ArgumentNullException.ThrowIfNull(classify);
        var outer = t_doubt;
        t_doubt = false;
        try
        {
            var result = classify();
            certain = !t_doubt;
            return result;
        }
        finally
        {
            t_doubt |= outer;
        }
    }

    /// <summary>Not streamable, because the classifier has no rule for the construct.</summary>
    private static PostureSweep Unmodelled()
    {
        t_doubt = true;
        return NotStreamable();
    }

    /// <summary>The answer depends on something the classifier does not know exactly.</summary>
    private static void Doubt() => t_doubt = true;

    // -----------------------------------------------------------------------
    // Static type: can the operand hold nodes with children (§19.8.1 rule 1(b)(iii)(A))
    // -----------------------------------------------------------------------

    /// <summary>
    /// The adjusted usage of an operand with usage absorption, from its static type (§19.8.1):
    /// 'I' (inspection) where the type allows no node with children, 'A' where it allows an
    /// element or a document node, 'a' where the classifier does not know the type. A 'a'
    /// operand is taken as absorption, and a verdict of "not streamable" that only absorption
    /// gives is then uncertain (see <see cref="GeneralRule"/>).
    /// </summary>
    private static char Absorption(XQueryExpression? e, StreamingContext ctx) => e switch
    {
        null => 'A',
        ContextItemExpression => ContextAbsorption(ctx),
        StepExpression step => Absorption(step, ctx),
        PathExpression { Steps.Count: > 0 } path => Absorption(path.Steps[^1], ctx),
        PathExpression { IsAbsolute: true, InitialExpression: null } => 'A',
        FilterExpression filter => Absorption(filter.Primary, ctx),
        // The type of a sequence is the union of the types of its items.
        SequenceExpression sequence => Union(sequence.Items, ctx),
        _ => 'a',
    };

    private static char Union(IReadOnlyList<XQueryExpression> items, StreamingContext ctx)
    {
        var usage = 'I';
        foreach (var item in items)
        {
            var one = Absorption(item, ctx);
            if (one == 'A')
                return 'A';
            if (one == 'a')
                usage = 'a';
        }
        return usage;
    }

    private static char Absorption(StepExpression step, StreamingContext ctx)
    {
        if (step.Axis is Axis.Attribute or Axis.Namespace)
            return 'I';
        if (step.NodeTest is KindTest { Kind: XdmNodeKind.Text or XdmNodeKind.Comment or XdmNodeKind.ProcessingInstruction
                or XdmNodeKind.Attribute or XdmNodeKind.Namespace })
            return 'I';
        if (step.Axis == Axis.Self)
            return step.NodeTest is NameTest or KindTest { Kind: XdmNodeKind.Element or XdmNodeKind.Document } ? 'A' : ContextAbsorption(ctx);
        // A name test selects elements on every other axis; element(), document-node() and
        // node() admit an element or a document node, and parents and ancestors are such nodes.
        if (step.NodeTest is NameTest or KindTest { Kind: XdmNodeKind.Element or XdmNodeKind.Document or XdmNodeKind.None })
            return 'A';
        return 'a';
    }

    private static char ContextAbsorption(StreamingContext ctx)
        => ctx.ContextHasNoChildren ? 'I' : ctx.ContextMayHaveChildren ? 'A' : 'a';

    /// <summary>
    /// Whether an expression of this shape selects nodes that may have children: for the
    /// context item type of the construct that it sets the focus for.
    /// </summary>
    private static bool MayHaveChildren(XQueryExpression e, StreamingContext ctx) => Absorption(e, ctx) == 'A';

    /// <summary>The absorption usage of a sequence constructor, from the instructions that return nodes.</summary>
    private static char Absorption(XsltSequenceConstructor? body, StreamingContext ctx)
    {
        if (body is null)
            return 'A';
        var usage = 'I';
        foreach (var instruction in body.Instructions)
        {
            var one = instruction switch
            {
                XsltSequence { Select: { } select } => Absorption(select, ctx),
                XsltSequence { Content: { } content } => Absorption(content, ctx),
                // These return no node of the streamed input.
                XsltVariableInstruction or XsltParamInstruction or XsltLiteralResultElement or XsltElement
                    or XsltAttribute or XsltValueOf or XsltText or XsltLiteralText or XsltTextValueTemplate
                    or XsltCopyOf or XsltComment or XsltProcessingInstruction or XsltNamespace
                    or XsltMessage or XsltAssert => 'I',
                _ => 'a',
            };
            if (one == 'A')
                return 'A';
            if (one == 'a')
                usage = 'a';
        }
        return usage;
    }

    // -----------------------------------------------------------------------
    // §19.8.3: a sequence constructor
    // -----------------------------------------------------------------------

    /// <summary>
    /// §19.8.3: the general rules, with each contained instruction an operand of usage
    /// transmission. More than one instruction that is potentially consuming makes the
    /// sequence constructor roaming and free-ranging, unless they are all motionless with one
    /// posture.
    /// </summary>
    private static PostureSweep ClassifySequenceConstructorBySpec(XsltSequenceConstructor body, StreamingContext ctx)
    {
        var operands = new List<(PostureSweep Operand, char Usage)>(body.Instructions.Count);
        var localCtx = ctx;
        foreach (var child in body.Instructions)
        {
            var ps = Classify(child, localCtx);
            if (ps.Sweep == Sweep.FreeRanging || !IsStreamablePosture(ps.Posture))
                return NotStreamable();
            operands.Add((ps, 'T'));
            if (ps.Sweep == Sweep.Consuming || (ctx.Handled is { } handled && RunsHandled(child, handled)))
                localCtx = localCtx with { AfterConsumingSibling = true };
            // §19.8.8.12: a variable is grounded; its binding (§19.8.4.41) cannot hold a
            // streamed node.
            if (child is XsltVariableInstruction { Name: var name })
                localCtx = BindVar(localCtx, name.LocalName, Posture.Grounded);
        }
        return GeneralRule(operands, atMostOneItem: false);
    }

    // -----------------------------------------------------------------------
    // §19.8.4: instructions that follow the general rules
    // -----------------------------------------------------------------------

    /// <summary>
    /// The posture and sweep of an instruction whose rule in §19.8.4 is the general rules with
    /// listed operand usages; null for any other instruction.
    /// </summary>
    private static PostureSweep? ClassifyInstructionBySpec(XsltInstruction insn, StreamingContext ctx)
    {
        var operands = new List<(PostureSweep Operand, char Usage)>();
        switch (insn)
        {
            // §19.8.4.1
            case XsltLiteralResultElement lre:
                if (AttributeSetsBySpec(lre.UseAttributeSets, ctx) is { } lreSets)
                    return lreSets;
                Absorb(operands, lre.Content, ctx);
                foreach (var avt in lre.Attributes.Values)
                    Absorb(operands, avt, ctx);
                break;
            // §19.8.4.15
            case XsltElement element:
                if (AttributeSetsBySpec(element.UseAttributeSets, ctx) is { } elementSets)
                    return elementSets;
                Absorb(operands, element.Name, ctx);
                Absorb(operands, element.Namespace, ctx);
                Absorb(operands, element.Content, ctx);
                break;
            // §19.8.4.12: the select (default ".") is inspected and the content absorbed, assessed
            // with the focus the select gives. By the letter of the rule a select and a content
            // that both consume are two consuming operands; the W3C suite expects them to stream
            // (si-copy-028, -047, -121 to -130: xsl:copy select="*" holding another one), and
            // Saxon-EE and Exselt stream them. Taken here as xsl:for-each over the selection (a
            // processor extension, §19.10): the content reads the selected node's subtree once.
            case XsltCopy copy:
            {
                if (AttributeSetsBySpec(copy.UseAttributeSets, ctx) is { } copySets)
                    return copySets;
                if (copy.Select is not { } select)
                {
                    operands.Add((new PostureSweep(ctx.ContextPosture, Sweep.Motionless), 'I'));
                    Absorb(operands, copy.Content, ctx);
                    break;
                }
                var selected = Classify(select, ctx);
                if (selected.Sweep == Sweep.FreeRanging || !IsStreamablePosture(selected.Posture))
                    return NotStreamable();
                if (copy.Content is not { } content)
                    return GeneralRule([(selected, 'I')], atMostOneItem: false);
                var inner = ctx with
                {
                    ContextPosture = selected.Posture, CurrentGroup = null, ContextHasNoChildren = SelectsNoChildren(select),
                    ContextMayHaveChildren = MayHaveChildren(select, ctx), AfterConsumingSibling = false,
                };
                var body = Classify(content, inner);
                if (body.Sweep == Sweep.FreeRanging || !IsStreamablePosture(body.Posture))
                    return NotStreamable();
                // As for xsl:for-each (§19.8.4.18): a crawling selection whose content consumes.
                if (selected.Posture == Posture.Crawling && body.Sweep != Sweep.Motionless)
                    return NotStreamable();
                return Grounded(CombineSweep(selected.Sweep, body.Sweep));
            }
            // §19.8.4.14
            case XsltDocument document:
                Absorb(operands, document.Content, ctx);
                break;
            // §19.8.4.35
            case XsltResultDocument result:
                foreach (var avt in new[]
                         {
                             result.Href, result.Format, result.Method, result.OmitXmlDeclaration, result.Encoding,
                             result.Standalone, result.OutputVersion, result.Indent, result.HtmlVersion,
                             result.DoctypePublic, result.DoctypeSystem, result.MediaType, result.IncludeContentType,
                             result.ByteOrderMark, result.EscapeUriAttributes, result.CdataSectionElements,
                             result.ItemSeparator, result.AllowDuplicateNames, result.ParameterDocument,
                         })
                    Absorb(operands, avt, ctx);
                Absorb(operands, result.Content, ctx);
                break;
            // §19.8.4.11
            case XsltComment comment:
                Absorb(operands, comment.Select, ctx);
                Absorb(operands, comment.Content, ctx);
                break;
            // §19.8.4.34
            case XsltProcessingInstruction pi:
                Absorb(operands, pi.Name, ctx);
                Absorb(operands, pi.Select, ctx);
                Absorb(operands, pi.Content, ctx);
                break;
            // §19.8.4.27
            case XsltNamespace ns:
                Absorb(operands, ns.Name, ctx);
                Absorb(operands, ns.Select, ctx);
                Absorb(operands, ns.Content, ctx);
                break;
            // §19.8.4.7
            case XsltAttribute attribute:
                Absorb(operands, attribute.Name, ctx);
                Absorb(operands, attribute.Namespace, ctx);
                Absorb(operands, attribute.Select, ctx);
                Absorb(operands, attribute.Separator, ctx);
                Absorb(operands, attribute.Content, ctx);
                break;
            // §19.8.4.40
            case XsltValueOf valueOf:
                Absorb(operands, valueOf.Select, ctx);
                Absorb(operands, valueOf.Separator, ctx);
                Absorb(operands, valueOf.Content, ctx);
                break;
            // §19.8.4.38
            case XsltText or XsltLiteralText:
                break;
            // §19.8.3: a text value template is absorbed.
            case XsltTextValueTemplate tvt:
                Absorb(operands, tvt.Template, ctx);
                break;
            // §19.8.4.36
            case XsltSequence sequence:
                if (sequence.Select is { } sequenceSelect)
                    operands.Add((Classify(sequenceSelect, ctx), 'T'));
                if (sequence.Content is { } sequenceContent)
                    operands.Add((Classify(sequenceContent, ctx), 'T'));
                break;
            // §19.8.4.26
            case XsltMessage message:
                Absorb(operands, message.Select, ctx);
                Absorb(operands, message.TerminateAvt, ctx);
                Absorb(operands, message.ErrorCodeAvt, ctx);
                Absorb(operands, message.Content, ctx);
                break;
            // §19.8.4.6
            case XsltAssert assert:
                operands.Add((Classify(assert.Test, ctx), 'I'));
                Absorb(operands, assert.Select, ctx);
                Absorb(operands, assert.ErrorCodeAvt, ctx);
                Absorb(operands, assert.Content, ctx);
                break;
            // §19.8.4.41: without "as", the select is navigated and the content absorbed; with
            // it, both have the usage the type determines.
            case XsltVariableInstruction variable:
            {
                if (variable.As is { } type)
                {
                    var usage = TypeDeterminedUsage(type);
                    if (variable.Select is { } typedSelect)
                        operands.Add((Classify(typedSelect, ctx), usage == 'A' ? Absorption(typedSelect, ctx) : usage));
                    if (variable.Content is { } typedContent)
                        operands.Add((Classify(typedContent, ctx), usage == 'A' ? Absorption(typedContent, ctx) : usage));
                }
                else
                {
                    if (variable.Select is { } select)
                        operands.Add((Classify(select, ctx), 'N'));
                    Absorb(operands, variable.Content, ctx);
                }
                break;
            }
            // §19.8.4.21
            case XsltIf iff:
                operands.Add((Classify(iff.Test, ctx), 'I'));
                operands.Add((Classify(iff.Then, ctx), 'T'));
                break;
            // §19.8.4.10
            case XsltChoose choose:
                return ClassifyChooseBySpec(choose, ctx);
            default:
                return null;
        }
        return GeneralRule(operands, atMostOneItem: false);
    }

    /// <summary>
    /// §19.8.4.10: the tests are inspected, and the branches are transmitted as a choice
    /// operand group: any number of branches may consume where no test does, or one test may
    /// consume where nothing else does.
    /// </summary>
    private static PostureSweep ClassifyChooseBySpec(XsltChoose choose, StreamingContext ctx)
    {
        var tests = new List<(PostureSweep Operand, char Usage)>(choose.When.Count);
        var branches = new List<PostureSweep>(choose.When.Count + 1);
        foreach (var when in choose.When)
        {
            tests.Add((Classify(when.Test, ctx), 'I'));
            branches.Add(Classify(when.Body, ctx));
        }
        if (choose.Otherwise is { } otherwise)
            branches.Add(Classify(otherwise, ctx));

        var consumingTests = 0;
        foreach (var (test, _) in tests)
        {
            if (test.Sweep == Sweep.FreeRanging || test.Posture == Posture.Roaming)
                return NotStreamable();
            if (test.Sweep == Sweep.Consuming)
                consumingTests++;
        }
        var potentiallyConsumingBranches = 0;
        foreach (var branch in branches)
        {
            if (branch.Sweep == Sweep.FreeRanging || !IsStreamablePosture(branch.Posture))
                return NotStreamable();
            if (branch.Sweep == Sweep.Consuming || branch.Posture != Posture.Grounded)
                potentiallyConsumingBranches++;
        }

        if (consumingTests > 1 || (consumingTests == 1 && potentiallyConsumingBranches > 0))
            return NotStreamable();
        if (consumingTests == 1)
            return Grounded(Sweep.Consuming);
        if (potentiallyConsumingBranches == 0)
            return Grounded(Sweep.Motionless);

        // The choice group: the combined posture of the branches and the widest sweep. The
        // branches that return nothing streamed do not change the posture.
        Posture? posture = null;
        var sweep = Sweep.Motionless;
        foreach (var branch in branches)
        {
            sweep = CombineSweep(sweep, branch.Sweep);
            if (branch.Posture == Posture.Grounded)
                continue;
            posture = posture is null ? branch.Posture : WidenNodePosture(posture.Value, branch.Posture, branches: true);
        }
        return new PostureSweep(posture ?? Posture.Grounded, sweep);
    }

    /// <summary>
    /// §19.8.4.1, .12 and .15: a reference to an attribute set that is not declared streamable
    /// makes the instruction roaming and free-ranging. Null when every set is declared
    /// streamable (or there is none); the not-streamable answer is uncertain when a set is not
    /// found in the stylesheet the classifier was given.
    /// </summary>
    private static PostureSweep? AttributeSetsBySpec(List<QName> names, StreamingContext ctx)
    {
        if (names.Count == 0 || ctx.ContextPosture == Posture.Grounded)
            return null;
        if (ctx.Stylesheet is not { } stylesheet)
            return Unmodelled();
        foreach (var name in names)
        {
            if (!stylesheet.AttributeSets.TryGetValue(name, out var set))
                return Unmodelled();
            if (!set.Streamable)
                return NotStreamable();
        }
        return null;
    }

    /// <summary>
    /// §19.8: the type-determined usage of an operand: inspection for a function type,
    /// absorption for an atomic type, navigation otherwise.
    /// </summary>
    private static char TypeDeterminedUsage(XdmSequenceType type)
        => type.ItemType is ItemType.Function or ItemType.Map or ItemType.Array ? 'I'
            : IsAtomicType(type) ? 'A'
            : 'N';

    private static void Absorb(List<(PostureSweep Operand, char Usage)> operands, XQueryExpression? expr, StreamingContext ctx)
    {
        if (expr is not null)
            operands.Add((Classify(expr, ctx), Absorption(expr, ctx)));
    }

    private static void Absorb(List<(PostureSweep Operand, char Usage)> operands, XsltSequenceConstructor? body, StreamingContext ctx)
    {
        if (body is not null)
            operands.Add((Classify(body, ctx), Absorption(body, ctx)));
    }

    /// <summary>Each expression of an attribute value template is an operand with usage absorption.</summary>
    private static void Absorb(List<(PostureSweep Operand, char Usage)> operands, XsltAttributeValueTemplate? avt, StreamingContext ctx)
    {
        if (avt is null)
            return;
        foreach (var part in avt.Parts)
        {
            if (part is AvtExpression ae)
                Absorb(operands, ae.Expression, ctx);
        }
    }
}
