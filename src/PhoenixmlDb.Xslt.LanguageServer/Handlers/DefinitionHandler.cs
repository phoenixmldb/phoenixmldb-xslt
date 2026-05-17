using System;
using System.Text.RegularExpressions;
using PhoenixmlDb.Xslt.LanguageServer.Lsp;
using Range = PhoenixmlDb.Xslt.LanguageServer.Lsp.Range;
using Position = PhoenixmlDb.Xslt.LanguageServer.Lsp.Position;

namespace PhoenixmlDb.Xslt.LanguageServer.Handlers;

/// <summary>
/// Server-side <c>textDocument/definition</c> for XSLT. Local resolution only.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>$varname</c> → matches <c>&lt;xsl:variable name="varname"</c> or
///         <c>&lt;xsl:param name="varname"</c>.</item>
///   <item>Bare identifier → matches <c>&lt;xsl:template name="identifier"</c>
///         (named-template call target).</item>
/// </list>
/// AST-free regex scan so incomplete stylesheets still resolve.
/// Cross-stylesheet (via <c>xsl:include</c>/<c>xsl:import</c>) is Plan 30+.
/// </remarks>
public static class DefinitionHandler
{
    public static Location? Handle(DocumentBuffer buf, Position pos)
    {
        ArgumentNullException.ThrowIfNull(buf);
        ArgumentNullException.ThrowIfNull(pos);

        var offset = PositionToOffset(buf.Text, pos);
        var token = ExtractTokenAt(buf.Text, offset);
        if (token is null) return null;

        Regex pattern;
        if (token.Value.IsVariable)
        {
            pattern = new Regex(@"<xsl:(?:variable|param)\s+(?:[^>]*?\s)?name\s*=\s*[""'](" +
                Regex.Escape(token.Value.Name) + @")[""']", RegexOptions.Compiled);
        }
        else
        {
            pattern = new Regex(@"<xsl:template\s+(?:[^>]*?\s)?name\s*=\s*[""'](" +
                Regex.Escape(token.Value.Name) + @")[""']", RegexOptions.Compiled);
        }

        var match = pattern.Match(buf.Text);
        if (!match.Success) return null;

        var group = match.Groups[1];
        var start = OffsetToPosition(buf.Text, group.Index);
        var end = OffsetToPosition(buf.Text, group.Index + group.Length);
        return new Location(buf.Uri, new Range(start, end));
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
