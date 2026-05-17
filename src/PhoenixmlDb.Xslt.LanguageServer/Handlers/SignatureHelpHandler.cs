using System;
using System.Collections.Generic;
using PhoenixmlDb.Xslt.LanguageServer.Lsp;

namespace PhoenixmlDb.Xslt.LanguageServer.Handlers;

/// <summary>
/// Server-side <c>textDocument/signatureHelp</c> for XSLT. XSLT documents are
/// XML, so most function calls live inside attribute values (typically
/// <c>select="fn(...)"</c>). The parens-walking algorithm doesn't care.
/// Curated signature table covers common <c>fn:*</c> + a few XSLT-specific
/// built-ins (<c>current()</c>, <c>document()</c>, etc.).
/// </summary>
public static class SignatureHelpHandler
{
    private static readonly Dictionary<string, string> Signatures = new(StringComparer.Ordinal)
    {
        ["current"] = "current() as item()",
        ["document"] = "document($uri-sequence as item()*) as document-node()*",
        ["format-number"] = "format-number($value as xs:numeric?, $picture as xs:string) as xs:string",
        ["system-property"] = "system-property($property-name as xs:string) as xs:string",
        ["unparsed-text"] = "unparsed-text($href as xs:string?) as xs:string?",
        ["unparsed-text-lines"] = "unparsed-text-lines($href as xs:string?) as xs:string*",
        ["key"] = "key($name as xs:string, $value as xs:anyAtomicType*) as node()*",
        ["abs"] = "abs($arg as xs:numeric?) as xs:numeric?",
        ["boolean"] = "boolean($arg as item()*) as xs:boolean",
        ["concat"] = "concat($arg1, $arg2, …) as xs:string",
        ["contains"] = "contains($arg1 as xs:string?, $arg2 as xs:string?) as xs:boolean",
        ["count"] = "count($arg as item()*) as xs:integer",
        ["data"] = "data($arg as item()*) as xs:anyAtomicType*",
        ["distinct-values"] = "distinct-values($arg as xs:anyAtomicType*) as xs:anyAtomicType*",
        ["empty"] = "empty($arg as item()*) as xs:boolean",
        ["exists"] = "exists($arg as item()*) as xs:boolean",
        ["local-name"] = "local-name($arg as node()?) as xs:string",
        ["matches"] = "matches($input as xs:string?, $pattern as xs:string) as xs:boolean",
        ["name"] = "name($arg as node()?) as xs:string",
        ["normalize-space"] = "normalize-space($arg as xs:string?) as xs:string",
        ["not"] = "not($arg as item()*) as xs:boolean",
        ["position"] = "position() as xs:integer",
        ["last"] = "last() as xs:integer",
        ["replace"] = "replace($input, $pattern, $replacement) as xs:string",
        ["starts-with"] = "starts-with($arg1 as xs:string?, $arg2 as xs:string?) as xs:boolean",
        ["string"] = "string($arg as item()?) as xs:string",
        ["string-join"] = "string-join($arg as xs:anyAtomicType*, $sep as xs:string?) as xs:string",
        ["string-length"] = "string-length($arg as xs:string?) as xs:integer",
        ["substring"] = "substring($source, $start, $length?) as xs:string",
        ["sum"] = "sum($arg as xs:anyAtomicType*) as xs:anyAtomicType?",
        ["tokenize"] = "tokenize($input as xs:string?, $pattern as xs:string) as xs:string*",
        ["upper-case"] = "upper-case($arg as xs:string?) as xs:string",
        ["lower-case"] = "lower-case($arg as xs:string?) as xs:string",
    };

    public static SignatureHelp? Handle(DocumentBuffer buf, Position pos)
    {
        ArgumentNullException.ThrowIfNull(buf);
        ArgumentNullException.ThrowIfNull(pos);
        var offset = PositionToOffset(buf.Text, pos);
        if (offset <= 0) return null;

        int depth = 0, activeParam = 0, parenIndex = -1;
        for (int i = offset - 1; i >= 0; i--)
        {
            var c = buf.Text[i];
            if (c == ')') depth++;
            else if (c == ',' && depth == 0) activeParam++;
            else if (c == '(')
            {
                if (depth == 0) { parenIndex = i; break; }
                depth--;
            }
        }
        if (parenIndex <= 0) return null;

        int start = parenIndex;
        while (start > 0 && IsWordChar(buf.Text[start - 1])) start--;
        if (start == parenIndex) return null;
        var fnName = StripPrefix(buf.Text.Substring(start, parenIndex - start));
        if (!Signatures.TryGetValue(fnName, out var sig)) return null;

        return new SignatureHelp(
            Signatures: [new SignatureInformation(sig, Documentation: null)],
            ActiveSignature: 0,
            ActiveParameter: activeParam);
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ':';

    private static string StripPrefix(string name)
    {
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? name[(colon + 1)..] : name;
    }

    private static int PositionToOffset(string text, Position pos)
    {
        int line = 0, col = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (line == pos.Line && col == pos.Character) return i;
            if (text[i] == '\n') { line++; col = 0; }
            else col++;
        }
        return text.Length;
    }
}
