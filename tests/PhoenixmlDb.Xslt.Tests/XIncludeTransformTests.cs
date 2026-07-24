using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A fatal XInclude expansion failure on the principal source (missing include, no fallback)
/// must surface as an <see cref="XsltException"/>, not a raw
/// <see cref="PhoenixmlDb.Core.Xml.XIncludeException"/> (XInclude SP2).
/// </summary>
public sealed class XIncludeTransformTests : IDisposable
{
    private readonly string _dir;

    public XIncludeTransformTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Fatal_xinclude_failure_surfaces_as_XsltException()
    {
        // Principal source references a missing include with NO fallback → fatal.
        var srcPath = Path.Combine(_dir, "src.xml");
        await File.WriteAllTextAsync(srcPath,
            "<doc xmlns:xi='http://www.w3.org/2001/XInclude'><xi:include href='missing.xml'/></doc>");
        const string xslt = "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
                             "<xsl:template match='/'><out/></xsl:template></xsl:stylesheet>";

        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xslt);
        t.SetSourceDocumentUri(new Uri(srcPath));
        t.EnableXInclude();

        var src = await File.ReadAllTextAsync(srcPath);
        var ex = await Assert.ThrowsAsync<XsltException>(() =>
            t.TransformAsync(src));
        ex.Message.Should().Contain("missing.xml");
    }
}
