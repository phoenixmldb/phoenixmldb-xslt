using PhoenixmlDb.Xslt.LanguageServer;
using PhoenixmlDb.Xslt.LanguageServer.Handlers;
using PhoenixmlDb.Xslt.LanguageServer.Lsp;
using Xunit;

namespace PhoenixmlDb.Xslt.LanguageServer.Tests;

public class DocumentSymbolTests
{
    [Fact]
    public void EmptyBufferProducesEmptyList()
    {
        var buf = new DocumentBuffer("file:///x.xsl", 1, "");
        Assert.Empty(DocumentSymbolHandler.Handle(buf));
    }

    [Fact]
    public void NamedTemplateProducesMethodSymbol()
    {
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template name="greet"><hello/></xsl:template>
            </xsl:stylesheet>
            """;
        var buf = new DocumentBuffer("file:///x.xsl", 1, xsl);
        var sym = Assert.Single(DocumentSymbolHandler.Handle(buf));
        Assert.Equal("greet", sym.Name);
        Assert.Equal(SymbolKind.Method, sym.Kind);
    }

    [Fact]
    public void TemplateWithMatchProducesMethodSymbol()
    {
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template match="/book"><out/></xsl:template>
            </xsl:stylesheet>
            """;
        var buf = new DocumentBuffer("file:///x.xsl", 1, xsl);
        var sym = Assert.Single(DocumentSymbolHandler.Handle(buf));
        Assert.Equal("/book", sym.Name);
    }

    [Fact]
    public void VariableProducesVariableSymbol()
    {
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:variable name="x" select="1"/>
            </xsl:stylesheet>
            """;
        var buf = new DocumentBuffer("file:///x.xsl", 1, xsl);
        var sym = Assert.Single(DocumentSymbolHandler.Handle(buf));
        Assert.Equal("$x", sym.Name);
        Assert.Equal(SymbolKind.Variable, sym.Kind);
    }
}
