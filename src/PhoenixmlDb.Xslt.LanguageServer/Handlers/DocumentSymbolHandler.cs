using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PhoenixmlDb.Xslt.LanguageServer.Lsp;
using Range = PhoenixmlDb.Xslt.LanguageServer.Lsp.Range;
using Position = PhoenixmlDb.Xslt.LanguageServer.Lsp.Position;

namespace PhoenixmlDb.Xslt.LanguageServer.Handlers;

/// <summary>
/// Extracts <c>xsl:template</c>, <c>xsl:variable</c>, <c>xsl:param</c>, and
/// <c>xsl:function</c> declarations from an XSLT source buffer. Uses a tolerant
/// regex scan rather than the compiled AST so incomplete / invalid stylesheets
/// still produce useful symbols.
/// </summary>
public static class DocumentSymbolHandler
{
    // Matches the opening tag of a top-level declaration. The <xsl:...> elements
    // are typically children of the <xsl:stylesheet> root, so they appear at indent
    // level 1 (or 0 if the stylesheet root is omitted). We match anywhere because
    // the regex doesn't try to be precise about scope.
    private static readonly Regex TemplateRe = new(
        @"<xsl:template\s+(?:[^>]*?\s)?(?:name|match)\s*=\s*[""']([^""']+)[""']",
        RegexOptions.Compiled);

    private static readonly Regex VariableRe = new(
        @"<xsl:(?:variable|param)\s+(?:[^>]*?\s)?name\s*=\s*[""']([^""']+)[""']",
        RegexOptions.Compiled);

    private static readonly Regex FunctionRe = new(
        @"<xsl:function\s+(?:[^>]*?\s)?name\s*=\s*[""']([^""']+)[""']",
        RegexOptions.Compiled);

    /// <summary>Scans <paramref name="buf"/> for XSLT declarations.</summary>
    public static DocumentSymbol[] Handle(DocumentBuffer buf)
    {
        ArgumentNullException.ThrowIfNull(buf);
        var symbols = new List<DocumentSymbol>();
        Scan(buf, TemplateRe, SymbolKind.Method, prefix: null, symbols);
        Scan(buf, VariableRe, SymbolKind.Variable, prefix: "$", symbols);
        Scan(buf, FunctionRe, SymbolKind.Function, prefix: null, symbols);
        return symbols.ToArray();
    }

    private static void Scan(DocumentBuffer buf, Regex regex, int kind, string? prefix, List<DocumentSymbol> sink)
    {
        var text = buf.Text;
        foreach (Match m in regex.Matches(text))
        {
            var pos = OffsetToPosition(text, m.Index);
            var end = OffsetToPosition(text, m.Index + m.Length);
            var name = (prefix ?? "") + m.Groups[1].Value;
            sink.Add(new DocumentSymbol(name, kind,
                new Range(pos, end),
                new Range(pos, end)));
        }
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
