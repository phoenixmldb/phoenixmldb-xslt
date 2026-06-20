using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// OP-bucket Phase 4 (Task B): under streaming, a sequence/aggregation function whose
/// COLLATION argument is a path navigating the streamed input must still raise
/// FOCH0002 for an unknown collation URI. The XQuery library already raises FOCH0002
/// for an unknown collation; the streaming bug was that the collation argument (a
/// non-first argument navigating the input) evaluated against the synthetic empty
/// document node, resolving to the empty sequence — so the function saw no collation
/// and silently used the default (codepoint/Ordinal). The fix routes such a select to
/// the whole-input buffer so the collation argument resolves against the real input.
/// Mirrors W3C strm cases sf-distinct-values-010, sf-index-of-054, sf-max-054, sf-min-054.
/// </summary>
public class StreamingCollationErrorTests
{
    private static async Task<string> Transform(string stylesheet, string inputXml, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"streaming-collation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, file), inputXml);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(stylesheet, new Uri(dir + "/"));
            t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return await t.TransformAsync((string?)null);
        }
        finally { Directory.Delete(dir, true); }
    }

    // The input carries the bogus collation URI as element content (mirrors the W3C
    // special.xml: <unknownCollation>http://…/collation/unknown</unknownCollation>).
    private const string Xml =
        "<special><unknownCollation>http://www.w3.org/2005/xpath-functions/collation/unknown</unknownCollation></special>";

    private static string Sheet(string select) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:template name="xsl:initial-template">
            <xsl:source-document streamable="yes" href="special.xml">
              <out><xsl:value-of select="{{select}}"/></out>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task DistinctValues_UnknownCollationFromInput_RaisesFOCH0002()
    {
        var sheet = Sheet("distinct-values(('a', 'b', 'a'), /special/unknownCollation)");
        Func<Task> act = async () => await Transform(sheet, Xml, "special.xml");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("FOCH0002");
    }

    [Fact]
    public async Task Max_UnknownCollationFromInput_RaisesFOCH0002()
    {
        var sheet = Sheet("max(('a', 'b', 'c'), special/unknownCollation)");
        Func<Task> act = async () => await Transform(sheet, Xml, "special.xml");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("FOCH0002");
    }

    [Fact]
    public async Task Min_UnknownCollationFromInput_RaisesFOCH0002()
    {
        var sheet = Sheet("min(('a', 'b', 'c'), special/unknownCollation)");
        Func<Task> act = async () => await Transform(sheet, Xml, "special.xml");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("FOCH0002");
    }

    [Fact]
    public async Task IndexOf_UnknownCollationFromInput_RaisesFOCH0002()
    {
        var sheet = Sheet("index-of(('a', 'b', 'c'), 'a', special/unknownCollation)");
        Func<Task> act = async () => await Transform(sheet, Xml, "special.xml");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("FOCH0002");
    }
}
