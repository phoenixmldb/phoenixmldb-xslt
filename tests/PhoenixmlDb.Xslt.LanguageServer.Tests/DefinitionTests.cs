using PhoenixmlDb.Xslt.LanguageServer;
using PhoenixmlDb.Xslt.LanguageServer.Handlers;
using PhoenixmlDb.Xslt.LanguageServer.Lsp;
using Xunit;

namespace PhoenixmlDb.Xslt.LanguageServer.Tests;

public class DefinitionTests
{
    [Fact]
    public void FindsVariableDeclaration()
    {
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:variable name="x" select="1"/>
              <xsl:template match="/"><out value="{$x}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var buf = new DocumentBuffer("file:///x.xsl", 1, xsl);
        // Caret on $x in the template body — find line 2 column 35 roughly
        var posOfRef = xsl.IndexOf("$x", System.StringComparison.Ordinal) + 1; // on the 'x' after $
        var (line, col) = OffsetToLineCol(xsl, posOfRef);
        var loc = DefinitionHandler.Handle(buf, new Position(line, col));
        Assert.NotNull(loc);
    }

    [Fact]
    public void FindsParamDeclaration()
    {
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template match="/">
                <xsl:param name="p" select="0"/>
                <out value="{$p}"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var buf = new DocumentBuffer("file:///x.xsl", 1, xsl);
        var posOfRef = xsl.IndexOf("$p", System.StringComparison.Ordinal) + 1;
        var (line, col) = OffsetToLineCol(xsl, posOfRef);
        var loc = DefinitionHandler.Handle(buf, new Position(line, col));
        Assert.NotNull(loc);
    }

    [Fact]
    public void FindsNamedTemplateFromCallTemplate()
    {
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template name="greet"><hello/></xsl:template>
              <xsl:template match="/"><xsl:call-template name="greet"/></xsl:template>
            </xsl:stylesheet>
            """;
        var buf = new DocumentBuffer("file:///x.xsl", 1, xsl);
        // Position on the "greet" in call-template name="greet"
        var posOfCallRef = xsl.LastIndexOf("greet", System.StringComparison.Ordinal) + 2;
        var (line, col) = OffsetToLineCol(xsl, posOfCallRef);
        var loc = DefinitionHandler.Handle(buf, new Position(line, col));
        Assert.NotNull(loc);
    }

    [Fact]
    public void UnknownTokenReturnsNull()
    {
        var buf = new DocumentBuffer("file:///x.xsl", 1, "<x>$unknown</x>");
        var loc = DefinitionHandler.Handle(buf, new Position(0, 6));
        Assert.Null(loc);
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
