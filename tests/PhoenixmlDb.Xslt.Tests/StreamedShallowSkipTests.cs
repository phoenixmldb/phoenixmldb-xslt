using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// on-no-match="shallow-skip" in a streamable mode applies templates to the children of an
/// element no rule matches, also when a nested xsl:apply-templates read that element whole.
/// There the built-in rule left the children to the streaming loop, which had already passed
/// them, so no rule below the element ran: <c>&lt;f/&gt;</c> where the unstreamed run gives
/// <c>&lt;f&gt;&lt;i/&gt;&lt;/f&gt;</c>. Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class StreamedShallowSkipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-shskip-" + Guid.NewGuid().ToString("N"));

    public StreamedShallowSkipTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "d.xml"),
            "<root><fields><data><id1>1</id1><id2>9</id2></data></fields><fields><data><id1>2</id1></data><data><x><id1>3</id1></x></data></fields></root>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> Run(string rules, bool streamable)
    {
        var yes = streamable ? "yes" : "no";
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:mode streamable="{{yes}}" on-no-match="shallow-skip"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="{{yes}}" href="d.xml"><xsl:apply-templates/></xsl:source-document>
              </xsl:template>
              <xsl:template match="id1"><i><xsl:value-of select="."/></i></xsl:template>
              {{rules}}
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    // Reached by the pass.
    [InlineData("<xsl:template match='fields'><f><xsl:apply-templates/></f></xsl:template>",
        "<f><i>1</i></f><f><i>2</i><i>3</i></f>")]
    // Reached by a nested xsl:apply-templates, which reads each fields element whole.
    [InlineData("<xsl:template match='fields'><f><xsl:apply-templates/></f></xsl:template><xsl:template match='root'><out><xsl:apply-templates/></out></xsl:template>",
        "<out><f><i>1</i></f><f><i>2</i><i>3</i></f></out>")]
    [InlineData("<xsl:template match='root'><out><xsl:apply-templates/></out></xsl:template>",
        "<out><i>1</i><i>2</i><i>3</i></out>")]
    public async Task The_rules_below_an_unmatched_element_run(string rules, string expected)
    {
        (await Run(rules, streamable: false)).Should().Be(expected, "unstreamed");
        (await Run(rules, streamable: true)).Should().Be(expected, "streamed");
    }
}
