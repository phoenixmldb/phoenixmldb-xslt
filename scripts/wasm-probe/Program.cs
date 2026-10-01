using PhoenixmlDb.Xslt;

// Exit 0 with the result on stdout, or exit 2 with the exception on stderr, so the gate's A/B
// classifies this like any other case.
try
{
    var t = new XsltTransformer();
    await t.LoadStylesheetAsync("""
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
          <xsl:template match="/"><out n="{count(//a)}"><xsl:apply-templates select="//a"/></out></xsl:template>
          <xsl:template match="a"><b><xsl:value-of select="upper-case(@v)"/></b></xsl:template>
        </xsl:stylesheet>
        """);
    Console.WriteLine(await t.TransformAsync("""<r><a v="x"/><a v="ß"/></r>"""));
    return 0;
}
#pragma warning disable CA1031
catch (Exception e)
#pragma warning restore CA1031
{
    Console.Error.WriteLine($"WASM {e.GetType().FullName}: {e.Message}");
    return 2;
}
