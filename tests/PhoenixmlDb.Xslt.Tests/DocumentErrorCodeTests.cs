using AwesomeAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// document() raises the errors of fn:doc (XSLT 3.0 §20.1): FODC0002 for a resource that cannot
/// be retrieved or parsed, FODC0005 only for a URI that is not valid. It raised FODC0005 for a
/// missing or malformed document too. The source here has no base URI, so a reference that is not
/// valid is not caught by resolving it: it is checked first, as fn:doc checks it.
/// </summary>
public sealed class DocumentErrorCodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "document-codes-" + Guid.NewGuid().ToString("N"));

    public DocumentErrorCodeTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "malformed.xml"), "<r>");
        File.WriteAllText(Path.Combine(_dir, "ok.xml"), "<ok/>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private async Task<string> CodeAsync(string select)
    {
        var stylesheet = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>"
            + "<xsl:template match='/'><o><xsl:copy-of select=\"" + select + "\"/></o></xsl:template></xsl:stylesheet>";
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet, new Uri(Path.Combine(_dir, "t.xsl")));
        var run = () => transformer.TransformAsync("<x/>");
        var error = (await run.Should().ThrowAsync<Exception>()).Which;
        // An XPath error carries its code in ErrorCode; an XSLT error starts its message with it.
        return error switch
        {
            PhoenixmlDb.XQuery.Execution.XQueryRuntimeException x => x.ErrorCode,
            PhoenixmlDb.XQuery.Functions.XQueryException x => x.ErrorCode,
            _ => error.Message.Split(':')[0],
        };
    }

    [Theory]
    [InlineData("missing.xml", "FODC0002")]
    [InlineData("malformed.xml", "FODC0002")]
    [InlineData("nosuchscheme:x", "FODC0002")]
    [InlineData("http://[bad", "FODC0005")]
    [InlineData("1x:y.xml", "FODC0005")]
    public async Task Document_raises_the_code_fn_doc_raises(string reference, string code)
    {
        (await CodeAsync($"doc('{reference}')")).Should().Be(code, "fn:doc");
        (await CodeAsync($"document('{reference}')")).Should().Be(code, "document(), one argument");
        (await CodeAsync($"document('{reference}', /)")).Should().Be(code, "document(), with a base node");
    }

    [Fact]
    public async Task A_document_that_can_be_read_is_returned()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(
            "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'><xsl:output omit-xml-declaration='yes'/>"
            + "<xsl:template match='/'><o><xsl:copy-of select=\"document('ok.xml')\"/></o></xsl:template></xsl:stylesheet>",
            new Uri(Path.Combine(_dir, "t.xsl")));
        (await transformer.TransformAsync("<x/>")).Trim().Should().Be("<o><ok/></o>");
    }
}
