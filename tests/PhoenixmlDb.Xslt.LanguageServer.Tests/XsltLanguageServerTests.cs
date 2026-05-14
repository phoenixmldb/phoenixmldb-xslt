using PhoenixmlDb.Xslt.LanguageServer;
using Xunit;

namespace PhoenixmlDb.Xslt.LanguageServer.Tests;

public class XsltLanguageServerTests
{
    private const string ValidStylesheet = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
          <xsl:template match="/">
            <out/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public void InitializeReturnsServerCapabilities()
    {
        var server = new XsltLanguageServer();
        var result = server.Initialize(null);
        Assert.NotNull(result.Capabilities);
        Assert.Equal(1, result.Capabilities.TextDocumentSync);
    }

    [Fact]
    public void ValidStylesheetProducesNoDiagnostics()
    {
        var server = new XsltLanguageServer();
        var buf = new DocumentBuffer("file:///test.xsl", 1, ValidStylesheet);
        var diags = server.ComputeDiagnostics(buf);
        Assert.Empty(diags);
    }

    [Fact]
    public void MalformedXmlProducesDiagnostic()
    {
        var server = new XsltLanguageServer();
        var buf = new DocumentBuffer("file:///test.xsl", 1, "<xsl:stylesheet>broken without close");
        var diags = server.ComputeDiagnostics(buf);
        Assert.NotEmpty(diags);
        Assert.Equal(1, diags[0].Severity); // Error
    }
}
