using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

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

    /// <summary>
    /// This attribute value template with other operands and every other property kept: for the rewrite of
    /// <c>root()</c> in a rule for the document node (Engine.RootOfDocumentRewrite, BUGS.md #166).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "An instance method, so that a property added later is copied here; RootOfDocumentRewriteTests checks it")]
    internal XsltAttributeValueTemplate WithParts(IReadOnlyList<AvtPart> parts) => new() { Parts = parts };
}
