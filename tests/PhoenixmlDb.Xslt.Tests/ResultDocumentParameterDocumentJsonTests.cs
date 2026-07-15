using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Coverage for <c>xsl:result-document/@parameter-document</c> driving JSON serialization,
/// mirroring W3C conformance case insn/result-document/result-document-1406. An href-less
/// result-document with <c>build-tree="no"</c> references an external serialization-parameters
/// document that sets <c>method="json"</c> and declares an <c>output:use-character-maps</c>.
/// The map must serialize as JSON (string values quoted), the character map must rewrite the
/// serialized key characters, and no <c>&lt;?xml?&gt;</c> declaration may be emitted.
/// </summary>
public sealed class ResultDocumentParameterDocumentJsonTests
{
    [Fact]
    public async Task ParameterDocument_DrivesJsonMethodAndCharacterMaps()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rd1406-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var paramsPath = Path.Combine(dir, "params.xml");
            await File.WriteAllTextAsync(paramsPath, """
                <output:serialization-parameters
                   xmlns:output="http://www.w3.org/2010/xslt-xquery-serialization">
                   <output:method value="json"/>
                   <output:use-character-maps>
                     <output:character-map character="a" map-string="AAA"/>
                     <output:character-map character="b" map-string="BBB"/>
                     <output:character-map character="c" map-string="CCC"/>
                   </output:use-character-maps>
                </output:serialization-parameters>
                """);

            const string ss = """
                <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                   <xsl:template name="xsl:initial-template">
                      <xsl:result-document parameter-document="params.xml" build-tree="no">
                         <xsl:map>
                            <xsl:map-entry key="'a'" select="'AAA'"/>
                            <xsl:map-entry key="'b'" select="'BBB'"/>
                            <xsl:map-entry key="'c'" select="'CCC'"/>
                            <xsl:map-entry key="'d'" select="'DDD'"/>
                            <xsl:map-entry key="'e'" select="'EEE'"/>
                            <xsl:map-entry key="'f'" select="'FFF'"/>
                            <xsl:map-entry key="'g'" select="'GGG'"/>
                         </xsl:map>
                      </xsl:result-document>
                   </xsl:template>
                </xsl:transform>
                """;

            var t = new XsltTransformer();
            t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            await t.LoadStylesheetAsync(ss, new Uri(Path.Combine(dir, "sheet.xsl")));
            var result = await t.TransformAsync((string?)null);

            result.Should().Be("""{"AAA":"AAA","BBB":"BBB","CCC":"CCC","d":"DDD","e":"EEE","f":"FFF","g":"GGG"}""");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
