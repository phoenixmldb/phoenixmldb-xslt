using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xdm.Serialization;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
// XPath 4.0 ordered map: insertion-order iteration as a structural guarantee.
// xslt keeps its existing default key-equality (pass EqualityComparer<object>.Default
// at each construction site) — this change is about iteration order only.
using OrderedXdmMap = PhoenixmlDb.XQuery.Execution.OrderedXdmMap;

namespace PhoenixmlDb.Xslt.Engine;

internal sealed partial class DefaultXsltExecutionContext
{

    /// <summary>
    /// Resolves NamespaceUri strings on NameTests to NamespaceIds using the node store.
    /// This bridges between parse-time prefix→URI resolution and execution-time URI→ID resolution.
    /// </summary>
    private void ResolveExpressionNamespaceIds(XQueryExpression expr)
    {
        // Traversal comes from XQueryExpressionWalker so every composite node kind is visited. The
        // hand-written switch this replaces missed some/every (and switch, typeswitch, try/catch, map
        // and array constructors, lookups): a name test inside one kept ResolvedNamespace unset and
        // matched only no-namespace nodes. InlineFunctionExpression was an earlier instance of the same
        // gap (see the note on NamespaceIdResolver below).
        if (expr is not null)
            new NamespaceIdResolver(this).Walk(expr);
    }

    /// <summary>
    /// Interns the namespace of every name test in an expression. An inline function body is ordinary
    /// XPath and needs the same interning: without it a prefixed step inside a closure —
    /// function($m) { $m/xdm:item } — matched only no-namespace nodes and a fold-left over it silently
    /// built an empty result (reported by Martin Honnen against xdm-persistence).
    /// </summary>
    private sealed class NamespaceIdResolver(DefaultXsltExecutionContext ctx) : PhoenixmlDb.XQuery.Ast.XQueryExpressionWalker
    {
        public override void Walk(XQueryExpression expr)
        {
            if (expr is not null)
                base.Walk(expr);
        }

        public override object? VisitStepExpression(StepExpression se)
        {
            if (se.NodeTest is NameTest nt)
            {
                // If prefix is set but URI wasn't resolved at parse time, resolve from stylesheet namespaces
                if (nt.Prefix != null && string.IsNullOrEmpty(nt.NamespaceUri) && nt.NamespaceUri != "*")
                {
                    if (ctx._stylesheet.Namespaces.TryGetValue(nt.Prefix, out var uri))
                        nt.NamespaceUri = uri;
                }
                // Apply stylesheet-level xpath-default-namespace for unprefixed element name tests
                else if (nt.Prefix == null && string.IsNullOrEmpty(nt.NamespaceUri) && !nt.IsLocalNameWildcard
                         && se.Axis != Axis.Attribute && se.Axis != Axis.Namespace
                         && ctx._stylesheet.XpathDefaultNamespace != null && !nt.ResolvedNamespace.HasValue)
                {
                    nt.NamespaceUri = ctx._stylesheet.XpathDefaultNamespace;
                }
                nt.ResolveNamespace(ctx._nodeStore!.InternNamespace);
            }
            return base.VisitStepExpression(se);
        }
    }


    /// <summary>
    /// Resolves namespace URIs in patterns to NamespaceIds using the node store.
    /// Needed for xsl:number count/from patterns which are not resolved during stylesheet setup.
    /// </summary>
    private void ResolvePatternNamespacesLocal(XsltPattern? pattern)
    {
        if (pattern == null || _nodeStore == null)
            return;
        switch (pattern)
        {
            case PathPattern pp:
                foreach (var step in pp.Steps)
                    if (step.NodeTest is NameTest nt && !nt.ResolvedNamespace.HasValue)
                        nt.ResolveNamespace(_nodeStore.InternNamespace);
                break;
            case UnionPattern up:
                foreach (var p in up.Patterns)
                    ResolvePatternNamespacesLocal(p);
                break;
            case ExceptPattern ep:
                ResolvePatternNamespacesLocal(ep.Left);
                ResolvePatternNamespacesLocal(ep.Right);
                break;
            case IntersectPattern ip:
                ResolvePatternNamespacesLocal(ip.Left);
                ResolvePatternNamespacesLocal(ip.Right);
                break;
        }
    }


    /// <summary>
    /// Extract namespace bindings from an element (XDM or System.Xml) for xsl:evaluate namespace-context.
    /// </summary>
    private Dictionary<string, string> ExtractNamespaceBindings(object? element)
    {
        var bindings = new Dictionary<string, string>();
        switch (element)
        {
            case Xdm.Nodes.XdmElement xdmElem:
            {
                // XDM element from in-memory node store — read NamespaceDeclarations
                foreach (var nsDecl in xdmElem.NamespaceDeclarations)
                {
                    var prefix = nsDecl.Prefix ?? "";
                    var uri = _nodeStore?.GetNamespaceUri(nsDecl.Namespace) ?? "";
                    if (prefix != "xml" && !bindings.ContainsKey(prefix))
                        bindings[prefix] = uri;
                }
                // Also add the element's own namespace if it has a prefix binding not yet covered
                if (xdmElem.Namespace != NamespaceId.None)
                {
                    var elemUri = _nodeStore?.GetNamespaceUri(xdmElem.Namespace) ?? "";
                    var elemPrefix = xdmElem.Prefix ?? "";
                    if (!bindings.ContainsKey(elemPrefix) && elemPrefix != "xml")
                        bindings[elemPrefix] = elemUri;
                }
                break;
            }
            case System.Xml.XmlElement xmlElem:
            {
                var nav = xmlElem.CreateNavigator();
                if (nav != null)
                {
                    foreach (var kvp in nav.GetNamespacesInScope(System.Xml.XmlNamespaceScope.All))
                        if (kvp.Key != "xml") bindings[kvp.Key] = kvp.Value;
                }
                break;
            }
            case System.Xml.Linq.XElement xElem:
            {
                foreach (var attr in xElem.Attributes())
                {
                    if (attr.IsNamespaceDeclaration)
                    {
                        var p = attr.Name.LocalName == "xmlns" ? "" : attr.Name.LocalName;
                        if (!bindings.ContainsKey(p) && p != "xml")
                            bindings[p] = attr.Value;
                    }
                }
                // Walk ancestors for inherited namespace declarations
                var ancestor = xElem.Parent;
                while (ancestor != null)
                {
                    foreach (var attr in ancestor.Attributes())
                    {
                        if (attr.IsNamespaceDeclaration)
                        {
                            var p = attr.Name.LocalName == "xmlns" ? "" : attr.Name.LocalName;
                            if (!bindings.ContainsKey(p) && p != "xml")
                                bindings[p] = attr.Value;
                        }
                    }
                    ancestor = ancestor.Parent;
                }
                break;
            }
            case System.Xml.XPath.XPathNavigator nav:
            {
                if (nav.NodeType == System.Xml.XPath.XPathNodeType.Element)
                {
                    foreach (var kvp in nav.GetNamespacesInScope(System.Xml.XmlNamespaceScope.All))
                        if (kvp.Key != "xml") bindings[kvp.Key] = kvp.Value;
                }
                break;
            }
        }
        return bindings;
    }


    /// <summary>
    /// Resolve namespace prefixes in a dynamically parsed XPath expression at runtime.
    /// Similar to StylesheetParser.ResolveExpressionNamespaces but works with a prefix→URI dictionary.
    /// </summary>
    private void ResolveExpressionNamespacesRuntime(XQueryExpression expr, Dictionary<string, string> nsBindings, string? xpathDefaultNs)
    {
        // Traversal comes from XQueryExpressionWalker so every composite node kind is visited; the
        // hand-written switch this replaces missed some/every, castable, treat, switch, typeswitch,
        // try/catch, map and array constructors and lookups, leaving their name tests unbound.
        if (expr is not null)
            new RuntimeNamespaceResolver(this, nsBindings, xpathDefaultNs).Walk(expr);
    }

    private sealed class RuntimeNamespaceResolver(
        DefaultXsltExecutionContext ctx, Dictionary<string, string> nsBindings, string? xpathDefaultNs)
        : PhoenixmlDb.XQuery.Ast.XQueryExpressionWalker
    {
        public override void Walk(XQueryExpression expr)
        {
            if (expr is not null)
                base.Walk(expr);
        }

        public override object? VisitVariableReference(PhoenixmlDb.XQuery.Ast.VariableReference vr)
        {
            if (!string.IsNullOrEmpty(vr.Name.Prefix) && vr.Name.Namespace == NamespaceId.None)
                vr.Name = ctx.ResolveQNameRuntime(vr.Name.Prefix!, vr.Name.LocalName, nsBindings);
            return null;
        }

        public override object? VisitFunctionCallExpression(PhoenixmlDb.XQuery.Ast.FunctionCallExpression fc)
        {
            if (!string.IsNullOrEmpty(fc.Name.Prefix) && fc.Name.Namespace == NamespaceId.None)
                fc.Name = ctx.ResolveQNameRuntime(fc.Name.Prefix!, fc.Name.LocalName, nsBindings);
            return base.VisitFunctionCallExpression(fc);
        }

        // f:name#1 names a function as a call does, so it resolves against the same bindings.
        // Left unresolved, the reference could not be matched to the stylesheet function it
        // names, and xsl:evaluate's check for private functions missed it.
        public override object? VisitNamedFunctionRef(PhoenixmlDb.XQuery.Ast.NamedFunctionRef nf)
        {
            if (!string.IsNullOrEmpty(nf.Name.Prefix) && nf.Name.Namespace == NamespaceId.None)
                nf.Name = ctx.ResolveQNameRuntime(nf.Name.Prefix!, nf.Name.LocalName, nsBindings);
            return base.VisitNamedFunctionRef(nf);
        }

        public override object? VisitStepExpression(PhoenixmlDb.XQuery.Ast.StepExpression se)
        {
            if (se.NodeTest is PhoenixmlDb.XQuery.Ast.NameTest nt)
            {
                if (!string.IsNullOrEmpty(nt.Prefix) && nt.Prefix != "*" && nt.NamespaceUri == null)
                {
                    if (nsBindings.TryGetValue(nt.Prefix, out var ns))
                        nt.NamespaceUri = ns;
                }
                else if (nt.Prefix == null && nt.NamespaceUri == null && !nt.IsLocalNameWildcard
                         && se.Axis != PhoenixmlDb.XQuery.Ast.Axis.Attribute
                         && se.Axis != PhoenixmlDb.XQuery.Ast.Axis.Namespace)
                {
                    if (xpathDefaultNs != null)
                        nt.NamespaceUri = xpathDefaultNs;
                }
            }
            return base.VisitStepExpression(se);
        }
    }
}
