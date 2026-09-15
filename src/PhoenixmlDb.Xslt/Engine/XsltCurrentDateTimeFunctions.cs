using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.XQuery.Functions;

namespace PhoenixmlDb.Xslt.Engine;

// fn:current-dateTime, fn:current-date and fn:current-time are deterministic: F&O 3.1 §16.6 requires the same value
// throughout one execution scope, and in XSLT that scope is the whole transformation. The XQuery versions read
// QueryExecutionContext.CurrentDateTime, which that context captures in its constructor, and the XSLT engine builds a
// fresh QueryExecutionContext for each XPath evaluation. So two calls in different expressions of one transformation
// could differ by however long the transformation took between them (xslt#107). These overrides read one
// CurrentDateTimeSnapshot shared by the whole transformation instead. The values are built exactly as the XQuery
// versions build them.

/// <summary>fn:current-dateTime() as xs:dateTime, stable for the whole transformation.</summary>
internal sealed class XsltCurrentDateTimeFunction(CurrentDateTimeSnapshot snapshot) : XQueryFunction
{
    public override QName Name => new(FunctionNamespaces.Fn, "current-dateTime");
    public override XdmSequenceType ReturnType => new() { ItemType = ItemType.DateTime, Occurrence = Occurrence.ExactlyOne };
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];

    public override ValueTask<object?> InvokeAsync(IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
        => ValueTask.FromResult<object?>(new Xdm.XsDateTime(snapshot.Now, true));
}

/// <summary>fn:current-date() as xs:date, stable for the whole transformation.</summary>
internal sealed class XsltCurrentDateFunction(CurrentDateTimeSnapshot snapshot) : XQueryFunction
{
    public override QName Name => new(FunctionNamespaces.Fn, "current-date");
    public override XdmSequenceType ReturnType => new() { ItemType = ItemType.Date, Occurrence = Occurrence.ExactlyOne };
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];

    public override ValueTask<object?> InvokeAsync(IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var now = snapshot.Now;
        return ValueTask.FromResult<object?>(new Xdm.XsDate(DateOnly.FromDateTime(now.DateTime), now.Offset));
    }
}

/// <summary>fn:current-time() as xs:time, stable for the whole transformation.</summary>
internal sealed class XsltCurrentTimeFunction(CurrentDateTimeSnapshot snapshot) : XQueryFunction
{
    public override QName Name => new(FunctionNamespaces.Fn, "current-time");
    public override XdmSequenceType ReturnType => new() { ItemType = ItemType.Time, Occurrence = Occurrence.ExactlyOne };
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];

    public override ValueTask<object?> InvokeAsync(IReadOnlyList<object?> arguments, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var now = snapshot.Now;
        var fracTicks = (int)(now.Ticks % TimeSpan.TicksPerSecond);
        return ValueTask.FromResult<object?>(new Xdm.XsTime(TimeOnly.FromDateTime(now.DateTime), now.Offset, fracTicks));
    }
}
