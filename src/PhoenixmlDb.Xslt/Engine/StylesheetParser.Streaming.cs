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

    private static void ValidateStreamableTemplates(XsltStylesheet stylesheet)
    {
        // Collect all streamable mode names
        var streamableModes = new HashSet<QName>();
        foreach (var (name, mode) in stylesheet.Modes)
        {
            if (mode.Streamable)
                streamableModes.Add(name);
        }

        if (streamableModes.Count == 0) return;

        // Check each template that participates in a streamable mode
        foreach (var template in stylesheet.Templates)
        {
            if (template.Match == null) continue; // Named-only templates aren't in any mode

            bool inStreamableMode = false;
            foreach (var mode in template.Modes)
            {
                if (mode.Equals(TemplateIndex.AllModeSentinel))
                {
                    // #all mode: template participates in all modes including streamable ones
                    inStreamableMode = true;
                    break;
                }
                // Map #default sentinel to the unnamed mode key
                var lookupKey = mode.Equals(TemplateIndex.DefaultModeSentinel) ? new QName(NamespaceId.None, "") : mode;
                if (streamableModes.Contains(lookupKey))
                {
                    inStreamableMode = true;
                    break;
                }
            }

            // If no mode specified, template is in the default mode
            if (template.Modes.Count == 0)
            {
                var unnamedKey = new QName(NamespaceId.None, "");
                if (streamableModes.Contains(unnamedKey))
                    inStreamableMode = true;
            }

            if (inStreamableMode)
            {
                StreamabilityChecker.CheckStreamablePattern(template.Match, null);
                StreamabilityChecker.CheckStreamableTemplateBody(template.Body, null, stylesheet.AttributeSets, stylesheet.Functions);
            }
        }
    }


    /// <summary>
    /// Parses xsl:stream — an alias for xsl:source-document with streamable="yes" (XSLT 3.0 §8.4).
    /// </summary>
    private XsltSourceDocument ParseStream(XElement element, SourceLocation? location)
        => ParseSourceDocument(element, location, forceStreamable: true);


    /// <summary>
    /// Walks an XQuery expression tree and applies <paramref name="visit"/> to every
    /// node. Mirrors the structure of <see cref="ResolveExpressionNamespaces"/> /
    /// <see cref="ContainsVariableReference"/> but factored as a generic post-order walk.
    /// </summary>
    private static void WalkExpressions(XQueryExpression expr, Action<XQueryExpression> visit)
    {
        visit(expr);
        switch (expr)
        {
            case BinaryExpression be:
                WalkExpressions(be.Left, visit);
                WalkExpressions(be.Right, visit);
                break;
            case UnaryExpression ue:
                WalkExpressions(ue.Operand, visit);
                break;
            case PathExpression pe:
                if (pe.InitialExpression != null) WalkExpressions(pe.InitialExpression, visit);
                foreach (var s in pe.Steps) WalkExpressions(s, visit);
                break;
            case StepExpression se:
                foreach (var p in se.Predicates) WalkExpressions(p, visit);
                break;
            case FilterExpression fe:
                WalkExpressions(fe.Primary, visit);
                foreach (var p in fe.Predicates) WalkExpressions(p, visit);
                break;
            case IfExpression ie:
                WalkExpressions(ie.Condition, visit);
                WalkExpressions(ie.Then, visit);
                if (ie.Else != null) WalkExpressions(ie.Else, visit);
                break;
            case FunctionCallExpression fc:
                foreach (var a in fc.Arguments) WalkExpressions(a, visit);
                break;
            case SequenceExpression seq:
                foreach (var i in seq.Items) WalkExpressions(i, visit);
                break;
            case InstanceOfExpression inst:
                WalkExpressions(inst.Expression, visit);
                break;
            case CastExpression cast:
                WalkExpressions(cast.Expression, visit);
                break;
            case CastableExpression castable:
                WalkExpressions(castable.Expression, visit);
                break;
            case TreatExpression treat:
                WalkExpressions(treat.Expression, visit);
                break;
            case SimpleMapExpression sme:
                WalkExpressions(sme.Left, visit);
                WalkExpressions(sme.Right, visit);
                break;
            case StringConcatExpression sce:
                foreach (var o in sce.Operands) WalkExpressions(o, visit);
                break;
            case RangeExpression re:
                WalkExpressions(re.Start, visit);
                WalkExpressions(re.End, visit);
                break;
            case ArrowExpression ae:
                WalkExpressions(ae.Expression, visit);
                WalkExpressions(ae.FunctionCall, visit);
                break;
            case InlineFunctionExpression ife:
                if (ife.Body != null) WalkExpressions(ife.Body, visit);
                break;
            case DynamicFunctionCallExpression dfc:
                WalkExpressions(dfc.FunctionExpression, visit);
                foreach (var a in dfc.Arguments) WalkExpressions(a, visit);
                break;
            case FlworExpression flwor:
                foreach (var clause in flwor.Clauses)
                {
                    if (clause is ForClause forClause)
                        foreach (var b in forClause.Bindings) WalkExpressions(b.Expression, visit);
                    else if (clause is LetClause letClause)
                        foreach (var b in letClause.Bindings) WalkExpressions(b.Expression, visit);
                    else if (clause is WhereClause whereClause)
                        WalkExpressions(whereClause.Condition, visit);
                    else if (clause is OrderByClause orderBy)
                        foreach (var s in orderBy.OrderSpecs) WalkExpressions(s.Expression, visit);
                }
                WalkExpressions(flwor.ReturnExpression, visit);
                break;
        }
    }

}
