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
