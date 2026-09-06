using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Literal result element.
/// </summary>
public sealed class XsltLiteralResultElement : XsltInstruction
{
    public required QName Name { get; init; }

    /// <summary>
    /// The element's namespace URI as written in the stylesheet (empty string for no namespace).
    /// <see cref="Name"/> only carries the source prefix (its <see cref="QName.Namespace"/> is
    /// unresolved), so this is the authoritative expanded-namespace for serialization-time
    /// matching such as <c>cdata-section-elements</c>. (W3C decl/output output-0138.)
    /// </summary>
    public string? SourceNamespaceName { get; init; }
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
