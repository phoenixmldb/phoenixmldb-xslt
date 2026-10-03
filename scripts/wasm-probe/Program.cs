using PhoenixmlDb.Xslt;

// Exit 0 with the result on stdout, or exit 2 with the exception on stderr, so the gate's A/B
// classifies this like any other case.
//
// No argument: one ordinary transform (xslt#237, every transform failed on browser-wasm).
// "deep-recursion": Martin Honnen's workbench example memo-function-fibonacci1.xsl at n = 1000.
// browser-wasm runs inline on its small default stack (no large-stack thread), and recursion
// that deep exhausted it (XTDE0000) on every build before xslt#266, which yields to unwind the
// stack. His own input (n = 200) sits at the stack's edge and passes or fails from run to run;
// n = 1000 fails reliably without #266 and stays within the 1200-level recursion limit.
try
{
    if (args.Length > 0 && args[0] == "deep-recursion")
    {
        var fib = new XsltTransformer();
        await fib.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="http://example.com/functions"
                exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="f:fib" as="xs:integer" cache="yes">
                <xsl:param name="num" as="xs:integer"/>
                <xsl:sequence select="if ($num = 0) then 0 else if ($num = 1) then 1 else f:fib($num - 2) + f:fib($num - 1)"/>
              </xsl:function>
              <xsl:template match="/"><xsl:value-of select="f:fib(xs:integer(/*))"/></xsl:template>
            </xsl:stylesheet>
            """);
        Console.WriteLine(await fib.TransformAsync("<data>1000</data>"));
        return 0;
    }

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
