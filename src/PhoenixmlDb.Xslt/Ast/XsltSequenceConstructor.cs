using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Represents a sequence constructor (content of templates, etc.).
/// </summary>
public sealed class XsltSequenceConstructor : XsltInstruction
{
    public required IReadOnlyList<XsltInstruction> Instructions { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitSequenceConstructor(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        // Check if we have on-empty or on-non-empty instructions
        bool hasOnEmpty = false, hasOnNonEmpty = false;
        for (int i = 0; i < Instructions.Count; i++)
        {
            if (Instructions[i] is XsltOnEmpty) hasOnEmpty = true;
            else if (Instructions[i] is XsltOnNonEmpty) hasOnNonEmpty = true;
        }

        if (!hasOnEmpty && !hasOnNonEmpty)
        {
            // Fast path: no conditional instructions
            foreach (var instruction in Instructions)
            {
                if (instruction.Location != null) context.PushInstructionLocation(instruction.Location);
                if (instruction.Version != null) context.PushVersion(instruction.Version);
                if (instruction.DefaultCollation != null) context.PushCollation(instruction.DefaultCollation);
                if (instruction.StaticBaseUri != null) context.PushStaticBaseUri(instruction.StaticBaseUri);
                try
                {
                    await instruction.ExecuteAsync(context).ConfigureAwait(false);
                }
                finally
                {
                    if (instruction.StaticBaseUri != null) context.PopStaticBaseUri();
                    if (instruction.DefaultCollation != null) context.PopCollation();
                    if (instruction.Version != null) context.PopVersion();
                    if (instruction.Location != null) context.PopInstructionLocation();
                }
            }
            return;
        }

        if (!hasOnNonEmpty)
        {
            // Only on-empty (must be at end per spec): efficient single-pass approach
            // Phase 1: execute non-conditional instructions, track content via output snapshot
            context.BeginContentTracking();
            foreach (var instruction in Instructions)
            {
                if (instruction is not XsltOnEmpty)
                {
                    if (instruction.Location != null) context.PushInstructionLocation(instruction.Location);
                    if (instruction.Version != null) context.PushVersion(instruction.Version);
                    if (instruction.DefaultCollation != null) context.PushCollation(instruction.DefaultCollation);
                    if (instruction.StaticBaseUri != null) context.PushStaticBaseUri(instruction.StaticBaseUri);
                    try { await instruction.ExecuteAsync(context).ConfigureAwait(false); }
                    finally { if (instruction.StaticBaseUri != null) context.PopStaticBaseUri(); if (instruction.DefaultCollation != null) context.PopCollation(); if (instruction.Version != null) context.PopVersion(); if (instruction.Location != null) context.PopInstructionLocation(); }
                }
            }
            bool wasPopulated = context.EndContentTracking();

            // Phase 2: execute on-empty only if no content was produced
            if (!wasPopulated)
            {
                foreach (var instruction in Instructions)
                {
                    if (instruction is XsltOnEmpty)
                    {
                        if (instruction.Version != null) context.PushVersion(instruction.Version);
                        if (instruction.DefaultCollation != null) context.PushCollation(instruction.DefaultCollation);
                        if (instruction.StaticBaseUri != null) context.PushStaticBaseUri(instruction.StaticBaseUri);
                        try { await instruction.ExecuteAsync(context).ConfigureAwait(false); }
                        finally { if (instruction.StaticBaseUri != null) context.PopStaticBaseUri(); if (instruction.DefaultCollation != null) context.PopCollation(); if (instruction.Version != null) context.PopVersion(); if (instruction.Location != null) context.PopInstructionLocation(); }
                    }
                }
            }
            return;
        }

        // Has on-non-empty (can appear anywhere in sequence constructor):
        // Two-pass approach — first determine if non-conditional content is produced,
        // then execute everything in order with conditionals resolved.
        // Phase 1: probe — execute non-conditional instructions to determine population
        var savedOutput = context.SaveOutput();
        context.BeginContentTracking();
        foreach (var instruction in Instructions)
        {
            if (instruction is not XsltOnEmpty and not XsltOnNonEmpty)
                await instruction.ExecuteAsync(context).ConfigureAwait(false);
        }
        bool wasPopulated2 = context.EndContentTracking();

        // Discard Phase 1 output and restore pre-Phase-1 state
        context.RestoreOutput(savedOutput);

        // Phase 2: execute all instructions in order with conditionals resolved.
        // When content is empty, suppress empty-string separators during non-conditional
        // instruction re-execution so separator spaces don't pollute the on-empty output.
        if (!wasPopulated2)
            context.SuppressEmptyStringSeparators();
        try
        {
            foreach (var instruction in Instructions)
            {
                if (instruction is XsltOnEmpty)
                {
                    if (!wasPopulated2)
                        await instruction.ExecuteAsync(context).ConfigureAwait(false);
                }
                else if (instruction is XsltOnNonEmpty)
                {
                    if (wasPopulated2)
                        await instruction.ExecuteAsync(context).ConfigureAwait(false);
                }
                else
                {
                    await instruction.ExecuteAsync(context).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (!wasPopulated2)
                context.RestoreEmptyStringSeparators();
        }
    }
}
