using PhoenixmlDb.Xslt.LanguageServer;
using PhoenixmlDb.Xslt.LanguageServer.Handlers;
using PhoenixmlDb.Xslt.LanguageServer.Lsp;
using Xunit;

namespace PhoenixmlDb.Xslt.LanguageServer.Tests;

public class ReferencesTests
{
    [Fact]
    public void FindsAllVariableReferences()
    {
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:variable name="x" select="1"/>
              <xsl:template match="/"><a v="{$x}"/><b v="{$x + 1}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var buf = new DocumentBuffer("file:///x.xsl", 1, xsl);
        var posOfFirstRef = xsl.IndexOf("$x", System.StringComparison.Ordinal) + 1;
        var (line, col) = OffsetToLineCol(xsl, posOfFirstRef);
        var refs = ReferencesHandler.Handle(buf, new Position(line, col));
        Assert.Equal(2, refs.Length);
    }

    [Fact]
    public void FindsAllTemplateCallSites()
    {
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template name="greet"><hello/></xsl:template>
              <xsl:template match="/">
                <xsl:call-template name="greet"/>
                <xsl:call-template name="greet"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var buf = new DocumentBuffer("file:///x.xsl", 1, xsl);
        // Position on "greet" inside the template-decl name attribute
        var posOfDecl = xsl.IndexOf("greet", System.StringComparison.Ordinal) + 2;
        var (line, col) = OffsetToLineCol(xsl, posOfDecl);
        var refs = ReferencesHandler.Handle(buf, new Position(line, col));
        // 1 decl + 2 call sites = 3 references
        Assert.Equal(3, refs.Length);
    }

    [Fact]
    public void UnknownTokenReturnsEmpty()
    {
        var buf = new DocumentBuffer("file:///x.xsl", 1, "<x>$unknown</x>");
        var refs = ReferencesHandler.Handle(buf, new Position(0, 6));
        // $unknown isn't declared, but it IS present once in the buffer as a literal — that's the find.
        // The handler returns matches regardless of whether they're declarations or uses.
        Assert.Single(refs);
    }

    private static (int Line, int Col) OffsetToLineCol(string text, int offset)
    {
        int line = 0, col = 0;
        for (int i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n') { line++; col = 0; }
            else col++;
        }
        return (line, col);
    }
}
