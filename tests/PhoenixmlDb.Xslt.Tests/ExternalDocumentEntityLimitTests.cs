using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xslt#627, BUGS.md #150: every document read from outside the transformation has the entity
/// limit of the principal source, 1,000,000 characters from entity expansion; fn:doc, document(),
/// fn:parse-xml, xsl:source-document, fn:transform's source-location and a global context document
/// given as text had the .NET default. At the limit the document is read; one character more, and
/// each path raises what it raises for a document it cannot read. (fn:parse-xml-fragment cannot
/// declare an entity: a fragment has no document type declaration.)
/// </summary>
public sealed class ExternalDocumentEntityLimitTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-627-").FullName;

    public ExternalDocumentEntityLimitTests()
    {
        File.WriteAllText(Path.Combine(_dir, "at.xml"), Document(0));
        File.WriteAllText(Path.Combine(_dir, "over.xml"), Document(1));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // 1,000 references to an entity of 1,000 characters: 1,000,000; plus <paramref name="more"/>
    // references to an entity of one character.
    private static string Document(int more)
        => "<!DOCTYPE r [<!ENTITY k '" + new string('x', 1000) + "'><!ENTITY o 'y'>]><r>"
            + string.Concat(Enumerable.Repeat("&k;", 1000)) + string.Concat(Enumerable.Repeat("&o;", more)) + "</r>";

    private string FileUri(string name) => new Uri(Path.Combine(_dir, name)).AbsoluteUri;

    private static async Task<string> Run(string body, string source = "<x/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(
            """<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"><xsl:output method="text"/><xsl:template match="/">"""
            + body + "</xsl:template></xsl:stylesheet>");
        return await t.TransformAsync(source);
    }

    private const string Inner =
        "&lt;xsl:stylesheet version=&quot;3.0&quot; xmlns:xsl=&quot;http://www.w3.org/1999/XSL/Transform&quot;&gt;"
        + "&lt;xsl:output method=&quot;text&quot;/&gt;&lt;xsl:template match=&quot;/&quot;&gt;"
        + "&lt;xsl:value-of select=&quot;string-length(/r)&quot;/&gt;&lt;/xsl:template&gt;&lt;/xsl:stylesheet&gt;";

    // The paths, as XPath or XSLT that reads the document {0} and writes the length of its text.
    public static TheoryData<string, string> Paths => new()
    {
        { "doc", """<xsl:value-of select="string-length(doc('{0}'))"/>""" },
        { "document", """<xsl:value-of select="string-length(document('{0}'))"/>""" },
        { "parse-xml", """<xsl:value-of select="string-length(parse-xml(unparsed-text('{0}')))"/>""" },
        { "source-document", """<xsl:source-document href="{0}" streamable="no"><xsl:value-of select="string-length(.)"/></xsl:source-document>""" },
        { "fn:transform", $$$"""<xsl:value-of select="transform(map{'stylesheet-text': '{{{Inner}}}', 'source-location': '{0}', 'delivery-format': 'raw'})?output"/>""" },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task A_document_at_the_limit_is_read(string path, string body)
        => (await Run(body.Replace("{0}", FileUri("at.xml"), StringComparison.Ordinal)))
            .Should().Be("1000000", path);

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task A_document_over_the_limit_is_not_read(string path, string body)
    {
        var run = () => Run(body.Replace("{0}", FileUri("over.xml"), StringComparison.Ordinal));

        // What each path raises for a document it cannot read: FODC0002 (fn:doc), FODC0005
        // (document()), FODC0006 (fn:parse-xml); the reader's own message, as for the principal
        // source (xsl:source-document, fn:transform's source-location).
        var error = (await run.Should().ThrowAsync<Exception>()).Which;
        var message = error switch
        {
            PhoenixmlDb.XQuery.Execution.XQueryRuntimeException x => x.ErrorCode + ": " + x.Message,
            PhoenixmlDb.XQuery.Functions.XQueryException x => x.ErrorCode + ": " + x.Message,
            _ => error.Message,
        };
        var expected = path switch
        {
            "doc" => "FODC0002",
            "document" => "FODC0005",
            "parse-xml" => "FODC0006",
            _ => "MaxCharactersFromEntities",
        };
        message.Should().Contain(expected, path);
    }

    [Fact]
    public async Task The_principal_source_has_the_same_limit()
    {
        (await Run("""<xsl:value-of select="string-length(.)"/>""", Document(0))).Should().Be("1000000");

        var run = () => Run("""<xsl:value-of select="string-length(.)"/>""", Document(1));
        (await run.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("MaxCharactersFromEntities");
    }

    [Fact]
    public void A_global_context_document_given_as_text_has_the_same_limit()
    {
        var t = new XsltTransformer();
        t.SetGlobalContextItem(Document(0));

        var act = () => t.SetGlobalContextItem(Document(1));

        act.Should().Throw<System.Xml.XmlException>().Which.Message.Should().Contain("MaxCharactersFromEntities");
    }

    /// <summary>A predefined entity or a character reference is no entity expansion.</summary>
    [Fact]
    public async Task Predefined_entities_and_character_references_do_not_count()
    {
        var file = Path.Combine(_dir, "refs.xml");
        await File.WriteAllTextAsync(file, "<r>" + string.Concat(Enumerable.Repeat("&lt;&amp;&#65;", 500_000)) + "</r>");

        (await Run($"""<xsl:value-of select="string-length(doc('{new Uri(file).AbsoluteUri}'))"/>""")).Should().Be("1500000");
    }
}
