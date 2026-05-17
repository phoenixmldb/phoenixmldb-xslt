using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PhoenixmlDb.Xslt.LanguageServer.Lsp;
using Range = PhoenixmlDb.Xslt.LanguageServer.Lsp.Range;
using Position = PhoenixmlDb.Xslt.LanguageServer.Lsp.Position;

namespace PhoenixmlDb.Xslt.LanguageServer.Handlers;

/// <summary>
/// Server-side <c>textDocument/references</c> for XSLT. Returns every occurrence
/// of the symbol at the given position.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>$varname</c> → all <c>\$varname\b</c> in the buffer.</item>
///   <item>template name → all <c>call-template name="name"</c> + the declaration's
///         own <c>&lt;xsl:template name="name"</c>.</item>
/// </list>
/// </remarks>
public static class ReferencesHandler
{
    public static Location[] Handle(DocumentBuffer buf, Position pos)
    {
        ArgumentNullException.ThrowIfNull(buf);
        ArgumentNullException.ThrowIfNull(pos);

        var offset = PositionToOffset(buf.Text, pos);
        var token = ExtractTokenAt(buf.Text, offset);
        if (token is null) return Array.Empty<Location>();

        var locations = new List<Location>();
        Regex regex;
        if (token.Value.IsVariable)
        {
            regex = new Regex(@"\$" + Regex.Escape(token.Value.Name) + @"\b", RegexOptions.Compiled);
        }
        else
        {
            // Match declarations + call-template references to the same name.
            regex = new Regex(
                @"(?:<xsl:(?:template|call-template)\s+(?:[^>]*?\s)?name\s*=\s*[""'])(" +
                Regex.Escape(token.Value.Name) + @")(?=[""'])",
                RegexOptions.Compiled);
        }

        foreach (Match m in regex.Matches(buf.Text))
        {
            // For variables, m.Index is at the $ — point at the name itself.
            int hitStart = m.Index;
            int hitLen = m.Length;
            if (token.Value.IsVariable)
            {
                hitStart += 1; // skip $
                hitLen -= 1;
            }
            else
            {
                // The captured name group is at Groups[1].
                hitStart = m.Groups[1].Index;
                hitLen = m.Groups[1].Length;
            }
            var startPos = OffsetToPosition(buf.Text, hitStart);
            var endPos = OffsetToPosition(buf.Text, hitStart + hitLen);
            locations.Add(new Location(buf.Uri, new Range(startPos, endPos)));
        }
        return locations.ToArray();
    }

    private static (string Name, bool IsVariable)? ExtractTokenAt(string text, int offset)
    {
        if (offset < 0 || offset > text.Length) return null;
        int start = offset;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        int end = offset;
        while (end < text.Length && IsWordChar(text[end])) end++;
        if (start == end) return null;
        var name = text.Substring(start, end - start);
        var isVariable = start > 0 && text[start - 1] == '$';
        return (name, isVariable);
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '-' || c == '_';

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

    private static Position OffsetToPosition(string text, int offset)
    {
        if (offset < 0) offset = 0;
        if (offset > text.Length) offset = text.Length;
        int line = 0, col = 0;
        for (int i = 0; i < offset; i++)
        {
            if (text[i] == '\n') { line++; col = 0; }
            else col++;
        }
        return new Position(line, col);
    }
}
