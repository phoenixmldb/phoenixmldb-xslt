using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>phx:indent-style="saxon-he-10"</c> (xslt#499) where a result is serialized: on xsl:output, on
/// xsl:result-document (literal and computed), through a named output format, on the primary result
/// written by xsl:result-document, and merged from several xsl:output declarations; and the errors
/// for a value that is not "saxon-he-10". The layout itself: <see cref="SaxonLayoutTests"/>; a
/// streamed result: StreamedSerializationTests. The expected result is Saxon-HE 10.9's.
/// </summary>
public class IndentStyleTests
{
    private const string Phx = "https://schemas.phoenixml.dev/2026/functions";

    private const string Tree = """<doc xmlns:q="urn:example:q" a-long-attribute-name1="value one" a-long-attribute-name2="value two" a-long-attribute-name3="value three"><item>t</item><item/><xsl:comment>c</xsl:comment></doc>""";

    private const string Expected =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<doc xmlns:q=\"urn:example:q\"\n" +
        "     a-long-attribute-name1=\"value one\"\n" +
        "     a-long-attribute-name2=\"value two\"\n" +
        "     a-long-attribute-name3=\"value three\">\n" +
        "   <item>t</item>\n" +
        "   <item/>\n" +
        "   <!--c-->\n" +
        "</doc>\n";

    private static string Stylesheet(string declarations, string body) => $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="{Phx}" exclude-result-prefixes="phx">
          {declarations}
          <xsl:template name="xsl:initial-template">{body}</xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<XsltTransformer> LoadAsync(string declarations, string body)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(Stylesheet(declarations, body));
        transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return transformer;
    }

    private static async Task<string> PrimaryAsync(string declarations, string body)
        => await (await LoadAsync(declarations, body)).TransformAsync((string?)null);

    private static async Task<string> SecondaryAsync(string declarations, string body)
    {
        var transformer = await LoadAsync(declarations, body);
        await transformer.TransformAsync((string?)null);
        return transformer.SecondaryResultDocuments.Values.Should().ContainSingle().Which;
    }

    [Fact]
    public async Task On_xsl_output()
        => (await PrimaryAsync("""<xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10"/>""", Tree))
            .Should().Be(Expected);

    [Fact]
    public async Task Without_it_the_layout_is_the_default_one()
        => (await PrimaryAsync("""<xsl:output method="xml" indent="yes"/>""", Tree))
            .Should().NotBe(Expected).And.Contain("\n  <item>t</item>");

    [Fact]
    public async Task From_two_xsl_output_declarations_merged()
        => (await PrimaryAsync("""<xsl:output method="xml"/><xsl:output indent="yes" phx:indent-style="saxon-he-10"/>""", Tree))
            .Should().Be(Expected);

    [Theory]
    [InlineData("phx:indent-style=\"saxon-he-10\"")]
    [InlineData("phx:indent-style=\"{'saxon-he-10'}\"")]
    public async Task On_xsl_result_document(string attribute)
        => (await SecondaryAsync("", $"""<xsl:result-document href="out.xml" method="xml" indent="yes" {attribute}>{Tree}</xsl:result-document><main/>"""))
            .Should().Be(Expected);

    [Fact]
    public async Task Through_the_output_format_of_xsl_result_document()
        => (await SecondaryAsync("""<xsl:output name="f" method="xml" indent="yes" phx:indent-style="saxon-he-10"/>""",
                $"""<xsl:result-document href="out.xml" format="f">{Tree}</xsl:result-document><main/>"""))
            .Should().Be(Expected);

    [Fact]
    public async Task On_the_primary_result_written_by_xsl_result_document()
        => (await PrimaryAsync("", $"""<xsl:result-document href="" method="xml" indent="yes" phx:indent-style="saxon-he-10">{Tree}</xsl:result-document>"""))
            .Should().Be(Expected);

    [Theory]
    [InlineData("""<xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-9"/>""", "")]
    [InlineData("", """<xsl:result-document href="out.xml" indent="yes" phx:indent-style="saxon"><a/></xsl:result-document>""")]
    public async Task Another_value_is_a_static_error(string declarations, string body)
    {
        var act = () => new XsltTransformer().LoadStylesheetAsync(Stylesheet(declarations, body));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE0020");
    }

    [Fact]
    public async Task Another_computed_value_is_a_dynamic_error()
    {
        var transformer = await LoadAsync("",
            """<xsl:result-document href="out.xml" indent="yes" phx:indent-style="{'saxon-he-' || 9}"><a/></xsl:result-document><main/>""");
        var act = () => transformer.TransformAsync((string?)null);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0030");
    }
}
