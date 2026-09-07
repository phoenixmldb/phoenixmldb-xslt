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
        switch (expr)
        {
            case StepExpression se:
                if (se.NodeTest is NameTest nt)
                {
                    // If prefix is set but URI wasn't resolved at parse time, resolve from stylesheet namespaces
                    if (nt.Prefix != null && string.IsNullOrEmpty(nt.NamespaceUri) && nt.NamespaceUri != "*")
                    {
                        if (_stylesheet.Namespaces.TryGetValue(nt.Prefix, out var uri))
                            nt.NamespaceUri = uri;
                    }
                    // Apply stylesheet-level xpath-default-namespace for unprefixed element name tests
                    else if (nt.Prefix == null && string.IsNullOrEmpty(nt.NamespaceUri) && !nt.IsLocalNameWildcard
                             && se.Axis != Axis.Attribute && se.Axis != Axis.Namespace
                             && _stylesheet.XpathDefaultNamespace != null && !nt.ResolvedNamespace.HasValue)
                    {
                        nt.NamespaceUri = _stylesheet.XpathDefaultNamespace;
                    }
                    nt.ResolveNamespace(_nodeStore!.InternNamespace);
                }
                foreach (var pred in se.Predicates)
                    ResolveExpressionNamespaceIds(pred);
                break;
            case PathExpression pe:
                if (pe.InitialExpression != null)
                    ResolveExpressionNamespaceIds(pe.InitialExpression);
                foreach (var step in pe.Steps)
                    ResolveExpressionNamespaceIds(step);
                break;
            case BinaryExpression be:
                ResolveExpressionNamespaceIds(be.Left);
                ResolveExpressionNamespaceIds(be.Right);
                break;
            case UnaryExpression ue:
                ResolveExpressionNamespaceIds(ue.Operand);
                break;
            case FilterExpression fe:
                ResolveExpressionNamespaceIds(fe.Primary);
                foreach (var pred in fe.Predicates)
                    ResolveExpressionNamespaceIds(pred);
                break;
            case FunctionCallExpression fc:
                foreach (var arg in fc.Arguments)
                    ResolveExpressionNamespaceIds(arg);
                break;
            case IfExpression ie:
                ResolveExpressionNamespaceIds(ie.Condition);
                ResolveExpressionNamespaceIds(ie.Then);
                if (ie.Else != null)
                    ResolveExpressionNamespaceIds(ie.Else);
                break;
            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    ResolveExpressionNamespaceIds(item);
                break;
            case FlworExpression flwor:
                foreach (var clause in flwor.Clauses)
                {
                    if (clause is ForClause forClause)
                        foreach (var binding in forClause.Bindings)
                            ResolveExpressionNamespaceIds(binding.Expression);
                    else if (clause is LetClause letClause)
                        foreach (var binding in letClause.Bindings)
                            ResolveExpressionNamespaceIds(binding.Expression);
                    else if (clause is WhereClause whereClause)
                        ResolveExpressionNamespaceIds(whereClause.Condition);
                    else if (clause is OrderByClause orderBy)
                        foreach (var spec in orderBy.OrderSpecs)
                            ResolveExpressionNamespaceIds(spec.Expression);
                }
                ResolveExpressionNamespaceIds(flwor.ReturnExpression);
                break;
            case SimpleMapExpression sme:
                ResolveExpressionNamespaceIds(sme.Left);
                ResolveExpressionNamespaceIds(sme.Right);
                break;
            case InstanceOfExpression inst:
                ResolveExpressionNamespaceIds(inst.Expression);
                break;
            case CastExpression cast:
                ResolveExpressionNamespaceIds(cast.Expression);
                break;
            case CastableExpression castable:
                ResolveExpressionNamespaceIds(castable.Expression);
                break;
            case TreatExpression treat:
                ResolveExpressionNamespaceIds(treat.Expression);
                break;
            case StringConcatExpression sce:
                foreach (var operand in sce.Operands)
                    ResolveExpressionNamespaceIds(operand);
                break;
            case RangeExpression re:
                ResolveExpressionNamespaceIds(re.Start);
                ResolveExpressionNamespaceIds(re.End);
                break;
            case ArrowExpression ae:
                ResolveExpressionNamespaceIds(ae.Expression);
                ResolveExpressionNamespaceIds(ae.FunctionCall);
                break;
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
        switch (expr)
        {
            case PhoenixmlDb.XQuery.Ast.VariableReference vr:
                if (!string.IsNullOrEmpty(vr.Name.Prefix) && vr.Name.Namespace == NamespaceId.None)
                    vr.Name = ResolveQNameRuntime(vr.Name.Prefix!, vr.Name.LocalName, nsBindings);
                break;
            case PhoenixmlDb.XQuery.Ast.FunctionCallExpression fc:
                if (!string.IsNullOrEmpty(fc.Name.Prefix) && fc.Name.Namespace == NamespaceId.None)
                    fc.Name = ResolveQNameRuntime(fc.Name.Prefix!, fc.Name.LocalName, nsBindings);
                foreach (var arg in fc.Arguments)
                    ResolveExpressionNamespacesRuntime(arg, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.BinaryExpression be:
                ResolveExpressionNamespacesRuntime(be.Left, nsBindings, xpathDefaultNs);
                ResolveExpressionNamespacesRuntime(be.Right, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.UnaryExpression ue:
                ResolveExpressionNamespacesRuntime(ue.Operand, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.PathExpression pe:
                if (pe.InitialExpression != null)
                    ResolveExpressionNamespacesRuntime(pe.InitialExpression, nsBindings, xpathDefaultNs);
                foreach (var step in pe.Steps)
                    ResolveExpressionNamespacesRuntime(step, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.StepExpression se:
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
                foreach (var pred in se.Predicates)
                    ResolveExpressionNamespacesRuntime(pred, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.FilterExpression fe:
                ResolveExpressionNamespacesRuntime(fe.Primary, nsBindings, xpathDefaultNs);
                foreach (var pred in fe.Predicates)
                    ResolveExpressionNamespacesRuntime(pred, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.IfExpression ie:
                ResolveExpressionNamespacesRuntime(ie.Condition, nsBindings, xpathDefaultNs);
                ResolveExpressionNamespacesRuntime(ie.Then, nsBindings, xpathDefaultNs);
                if (ie.Else != null)
                    ResolveExpressionNamespacesRuntime(ie.Else, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.FlworExpression flwor:
                foreach (var clause in flwor.Clauses)
                {
                    if (clause is PhoenixmlDb.XQuery.Ast.ForClause forClause)
                        foreach (var binding in forClause.Bindings)
                            ResolveExpressionNamespacesRuntime(binding.Expression, nsBindings, xpathDefaultNs);
                    else if (clause is PhoenixmlDb.XQuery.Ast.LetClause letClause)
                        foreach (var binding in letClause.Bindings)
                            ResolveExpressionNamespacesRuntime(binding.Expression, nsBindings, xpathDefaultNs);
                    else if (clause is PhoenixmlDb.XQuery.Ast.WhereClause whereClause)
                        ResolveExpressionNamespacesRuntime(whereClause.Condition, nsBindings, xpathDefaultNs);
                    else if (clause is PhoenixmlDb.XQuery.Ast.OrderByClause orderBy)
                        foreach (var spec in orderBy.OrderSpecs)
                            ResolveExpressionNamespacesRuntime(spec.Expression, nsBindings, xpathDefaultNs);
                }
                ResolveExpressionNamespacesRuntime(flwor.ReturnExpression, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.SequenceExpression seq:
                foreach (var item in seq.Items)
                    ResolveExpressionNamespacesRuntime(item, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.InstanceOfExpression inst:
                ResolveExpressionNamespacesRuntime(inst.Expression, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.CastExpression cast:
                ResolveExpressionNamespacesRuntime(cast.Expression, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.SimpleMapExpression sme:
                ResolveExpressionNamespacesRuntime(sme.Left, nsBindings, xpathDefaultNs);
                ResolveExpressionNamespacesRuntime(sme.Right, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.StringConcatExpression sce:
                foreach (var operand in sce.Operands)
                    ResolveExpressionNamespacesRuntime(operand, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.RangeExpression re:
                ResolveExpressionNamespacesRuntime(re.Start, nsBindings, xpathDefaultNs);
                ResolveExpressionNamespacesRuntime(re.End, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.ArrowExpression ae:
                ResolveExpressionNamespacesRuntime(ae.Expression, nsBindings, xpathDefaultNs);
                ResolveExpressionNamespacesRuntime(ae.FunctionCall, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.InlineFunctionExpression ife:
                if (ife.Body != null)
                    ResolveExpressionNamespacesRuntime(ife.Body, nsBindings, xpathDefaultNs);
                break;
            case PhoenixmlDb.XQuery.Ast.DynamicFunctionCallExpression dfc:
                ResolveExpressionNamespacesRuntime(dfc.FunctionExpression, nsBindings, xpathDefaultNs);
                foreach (var arg in dfc.Arguments)
                    ResolveExpressionNamespacesRuntime(arg, nsBindings, xpathDefaultNs);
                break;
        }
    }


    /// <summary>
    /// Rewrites the serialized XHTML-method output so that elements in the HTML5 content namespaces
    /// (XHTML, SVG, MathML) which are bound via a namespace <em>prefix</em> are re-expressed in the
    /// conventional default-namespace form: the element name becomes its local name and the
    /// namespace is declared as the default <c>xmlns</c> (dropping the now-unused
    /// <c>xmlns:prefix</c> declaration). Foreign-namespace elements, prefixed attributes, and
    /// elements already using the default namespace are left untouched. The input is well-formed,
    /// XML-escaped markup with no XML/DOCTYPE prolog yet prepended (this runs inside FinalizeOutput
    /// before those steps). Serialization 3.0, XHTML output method; W3C output-0211/0221/0225/0226.
    /// </summary>
    internal static string RedefaultXhtmlNamespaces(string output)
    {
        // Fast path: without a prefixed namespace declaration nothing uses a prefix.
        if (output.IndexOf("xmlns:", StringComparison.Ordinal) < 0)
            return output;

        static bool IsWs(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r';

        var sb = new StringBuilder(output.Length);
        // Input namespace scope: one frame per open element, prefix -> uri ("" = default namespace).
        // Used to resolve the namespace URI of element and attribute prefixes as they appeared in
        // the source result tree.
        var inputScopes = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, string>>();
        // Output namespace scope, tracking the declarations actually emitted. This lets prefix
        // normalization decide whether a stripped element still needs an xmlns default declaration
        // and whether a prefixed foreign-content attribute needs its xmlns:prefix re-declared (the
        // original ancestor declaration for the three HTML5 namespaces having been dropped).
        var outDefaults = new System.Collections.Generic.List<string>();
        var outPrefixDecls = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, string>>();
        // The element name to emit for each open element's matching end tag.
        var openNames = new System.Collections.Generic.List<string>();

        int i = 0, n = output.Length;
        while (i < n)
        {
            char c = output[i];
            if (c != '<')
            {
                sb.Append(c);
                i++;
                continue;
            }

            char c1 = i + 1 < n ? output[i + 1] : '\0';

            // Comments, CDATA sections, and declarations are copied verbatim.
            if (c1 == '!')
            {
                if (output.AsSpan(i).StartsWith("<!--"))
                {
                    int end = output.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 3;
                    sb.Append(output, i, end - i);
                    i = end;
                    continue;
                }
                if (output.AsSpan(i).StartsWith("<![CDATA["))
                {
                    int end = output.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 3;
                    sb.Append(output, i, end - i);
                    i = end;
                    continue;
                }
                int gt0 = output.IndexOf('>', i);
                gt0 = gt0 < 0 ? n : gt0 + 1;
                sb.Append(output, i, gt0 - i);
                i = gt0;
                continue;
            }
            if (c1 == '?')
            {
                int end = output.IndexOf("?>", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                sb.Append(output, i, end - i);
                i = end;
                continue;
            }
            if (c1 == '/')
            {
                int gt = output.IndexOf('>', i);
                if (gt < 0)
                {
                    sb.Append(output, i, n - i);
                    i = n;
                    continue;
                }
                string emit;
                if (openNames.Count > 0)
                {
                    emit = openNames[^1];
                    openNames.RemoveAt(openNames.Count - 1);
                }
                else
                {
                    emit = output.Substring(i + 2, gt - (i + 2));
                }
                if (inputScopes.Count > 0)
                    inputScopes.RemoveAt(inputScopes.Count - 1);
                if (outDefaults.Count > 0)
                    outDefaults.RemoveAt(outDefaults.Count - 1);
                if (outPrefixDecls.Count > 0)
                    outPrefixDecls.RemoveAt(outPrefixDecls.Count - 1);
                sb.Append("</").Append(emit).Append('>');
                i = gt + 1;
                continue;
            }

            // Start or empty-element tag: locate the closing '>' honoring quoted attribute values.
            int p = i + 1;
            while (p < n && output[p] != '>')
            {
                if (output[p] == '"' || output[p] == '\'')
                {
                    char q = output[p];
                    p++;
                    while (p < n && output[p] != q)
                        p++;
                }
                p++;
            }
            if (p >= n)
            {
                sb.Append(output, i, n - i);
                i = n;
                continue;
            }
            bool selfClose = output[p - 1] == '/';
            int innerEnd = selfClose ? p - 1 : p;
            string inner = output.Substring(i + 1, innerEnd - (i + 1));

            // Split element name from attribute text.
            int k = 0;
            while (k < inner.Length && !IsWs(inner[k]))
                k++;
            string qname = inner[..k];
            string attrsPart = inner[k..];

            // Parse attributes, preserving each attribute's leading whitespace so the tag can be
            // rebuilt verbatim minus any dropped namespace declaration.
            var attrs = new System.Collections.Generic.List<(string ws, string name, string eqValue)>();
            string trailing = "";
            int a = 0;
            while (a < attrsPart.Length)
            {
                int wsStart = a;
                while (a < attrsPart.Length && IsWs(attrsPart[a]))
                    a++;
                string ws = attrsPart[wsStart..a];
                if (a >= attrsPart.Length)
                {
                    trailing = ws;
                    break;
                }
                int nameStart = a;
                while (a < attrsPart.Length && attrsPart[a] != '=' && !IsWs(attrsPart[a]))
                    a++;
                string name = attrsPart[nameStart..a];
                while (a < attrsPart.Length && IsWs(attrsPart[a]))
                    a++;
                string eqValue = "";
                if (a < attrsPart.Length && attrsPart[a] == '=')
                {
                    int evStart = a;
                    a++;
                    while (a < attrsPart.Length && IsWs(attrsPart[a]))
                        a++;
                    if (a < attrsPart.Length && (attrsPart[a] == '"' || attrsPart[a] == '\''))
                    {
                        char q = attrsPart[a];
                        a++;
                        while (a < attrsPart.Length && attrsPart[a] != q)
                            a++;
                        if (a < attrsPart.Length)
                            a++;
                    }
                    eqValue = attrsPart[evStart..a];
                }
                attrs.Add((ws, name, eqValue));
            }

            // Namespace declarations on this element (input).
            var localDecls = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (_, name, eqValue) in attrs)
            {
                if (name == "xmlns")
                    localDecls[""] = ExtractQuotedValue(eqValue);
                else if (name.StartsWith("xmlns:", StringComparison.Ordinal))
                    localDecls[name[6..]] = ExtractQuotedValue(eqValue);
            }

            string prefix = "";
            string local = qname;
            int colon = qname.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                prefix = qname[..colon];
                local = qname[(colon + 1)..];
            }

            string InputLookup(string pfx)
            {
                if (localDecls.TryGetValue(pfx, out var lv))
                    return lv;
                for (int s = inputScopes.Count - 1; s >= 0; s--)
                    if (inputScopes[s].TryGetValue(pfx, out var v))
                        return v;
                return "";
            }
            string OutLookupPrefix(string pfx)
            {
                for (int s = outPrefixDecls.Count - 1; s >= 0; s--)
                    if (outPrefixDecls[s].TryGetValue(pfx, out var v))
                        return v;
                return "";
            }
            string OutDefault() => outDefaults.Count > 0 ? outDefaults[^1] : "";

            string elemUri = InputLookup(prefix);
            // "Prefix normalization" for the three HTML5 content namespaces (XHTML/SVG/MathML):
            // an element bound to one of them via a prefix is re-expressed with its local name and,
            // if necessary, a default xmlns declaration; the prefix is dropped.
            bool strip = prefix.Length > 0 && Html5ContentNamespaces.Contains(elemUri);
            bool unprefixedOut = strip || prefix.Length == 0;
            string outName = strip ? local : qname;

            // Decide the default (xmlns) declaration for this element and the resulting in-scope
            // default namespace for its children.
            bool hasOwnDefaultDecl = localDecls.ContainsKey("");
            string curDefault = OutDefault();
            string newDefault = curDefault;
            string? addDefaultDecl = null;
            if (unprefixedOut)
            {
                string wantUri = elemUri; // for prefix-less elements elemUri is the inherited/own default
                if (hasOwnDefaultDecl)
                    newDefault = localDecls[""]; // kept among the attributes below
                else if (wantUri != curDefault)
                {
                    addDefaultDecl = wantUri;
                    newDefault = wantUri;
                }
            }

            // Emit attributes, dropping declarations for the three HTML5 namespaces (they are
            // normalized away) and keeping foreign declarations. Track which prefixes remain
            // declared in the output so prefixed foreign-content attributes can be re-declared.
            var emittedPrefixDecls = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            var neededAttrDecls = new System.Collections.Generic.List<(string prefix, string uri)>();
            var attrsBuilder = new StringBuilder();
            foreach (var (ws, name, eqValue) in attrs)
            {
                if (name == "xmlns")
                {
                    attrsBuilder.Append(ws).Append(name).Append(eqValue);
                    continue;
                }
                if (name.StartsWith("xmlns:", StringComparison.Ordinal))
                {
                    string dpfx = name[6..];
                    string duri = ExtractQuotedValue(eqValue);
                    if (Html5ContentNamespaces.Contains(duri))
                        continue; // drop: prefix-normalized away (re-added below only if an attribute needs it)
                    attrsBuilder.Append(ws).Append(name).Append(eqValue);
                    emittedPrefixDecls[dpfx] = duri;
                    continue;
                }
                attrsBuilder.Append(ws).Append(name).Append(eqValue);
                // A prefixed attribute in one of the three HTML5 namespaces keeps its prefix
                // (attributes have no default namespace) and therefore needs its xmlns:prefix
                // declaration re-established on this element.
                int ac = name.IndexOf(':', StringComparison.Ordinal);
                if (ac > 0)
                {
                    string apfx = name[..ac];
                    string auri = InputLookup(apfx);
                    if (Html5ContentNamespaces.Contains(auri))
                        neededAttrDecls.Add((apfx, auri));
                }
            }

            var addedDeclSuffix = new StringBuilder();
            foreach (var (apfx, auri) in neededAttrDecls)
            {
                if (emittedPrefixDecls.TryGetValue(apfx, out var already) && already == auri)
                    continue;
                if (OutLookupPrefix(apfx) == auri)
                    continue;
                addedDeclSuffix.Append(" xmlns:").Append(apfx).Append("=\"").Append(auri).Append('"');
                emittedPrefixDecls[apfx] = auri;
            }

            sb.Append('<').Append(outName);
            if (addDefaultDecl != null)
                sb.Append(" xmlns=\"").Append(addDefaultDecl).Append('"');
            sb.Append(attrsBuilder);
            sb.Append(addedDeclSuffix);
            sb.Append(trailing);
            if (selfClose)
                sb.Append('/');
            sb.Append('>');

            if (!selfClose)
            {
                inputScopes.Add(new System.Collections.Generic.Dictionary<string, string>(localDecls, StringComparer.Ordinal));
                outDefaults.Add(newDefault);
                outPrefixDecls.Add(emittedPrefixDecls);
                openNames.Add(outName);
            }
            i = p + 1;
        }
        return sb.ToString();
    }

}
