using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Raises a dynamic error when executed (used for extension elements without xsl:fallback).
/// </summary>
public sealed class XsltDynamicError : XsltInstruction
{
    public required string ErrorCode { get; init; }
    public required string Message { get; init; }
    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitDynamicError(this);
    public override ValueTask ExecuteAsync(XsltExecutionContext context)
        => throw new PhoenixmlDb.Xslt.Engine.XsltException($"{ErrorCode}: {Message}");
}
