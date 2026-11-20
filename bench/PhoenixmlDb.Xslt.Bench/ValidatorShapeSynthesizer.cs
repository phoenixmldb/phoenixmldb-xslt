using System.Globalization;
using System.Text;

namespace PhoenixmlDb.Xslt.Bench;

// A seeded Random on purpose: the generated input must be the same on every run, so that two
// builds are measured on the same document. Nothing here is security-relevant.
#pragma warning disable CA5394

/// <summary>
/// Generates a stylesheet shaped like a compiled Schematron validator, and an input for it.
/// </summary>
/// <remarks>
/// The stylesheet has <c>modes</c> modes of <c>rules</c> template rules each. A rule matches
/// <c>eK[@a]</c> at a distinct priority and makes one literal result element with three
/// attributes, followed by <c>asserts</c> <c>xsl:choose</c> tests, one of which reads a
/// global <c>//@id</c> variable. Each mode also has a <c>text()</c> rule and an
/// <c>@*|node()</c> rule that continues with <c>*|comment()|processing-instruction()</c>, as the
/// Schematron skeleton does. The input is a tree of depth 8 to 12 whose element names are drawn
/// from <c>e0</c>..<c>e(1.5 x rules)</c>, so that some elements match no rule. Both are
/// deterministic for given parameters.
/// </remarks>
internal static class ValidatorShapeSynthesizer
{
    public static string Stylesheet(int modes, int rules, int asserts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">""");
        sb.AppendLine("""  <xsl:variable name="ids" select="//@id"/>""");
        sb.AppendLine("""  <xsl:template match="/">""");
        sb.AppendLine("""    <report>""");
        for (var m = 0; m < modes; m++)
            sb.AppendLine(CultureInfo.InvariantCulture, $"""      <pattern id="p{m}"><xsl:apply-templates select="/" mode="m{m}"/></pattern>""");
        sb.AppendLine("""    </report>""");
        sb.AppendLine("""  </xsl:template>""");
        for (var m = 0; m < modes; m++)
        {
            for (var k = 0; k < rules; k++)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"""  <xsl:template match="e{k}[@a]" mode="m{m}" priority="{rules - k + 1}">""");
                sb.Append("    <fired-rule role=\"r").Append(m).Append('-').Append(k).AppendLine("\" a=\"{@a}\"/>");
                for (var a = 0; a < asserts; a++)
                {
                    // The first assert reads the global //@id variable, as a validator's key-like lookups do;
                    // it compares with one id, so that the cost measured is the binding of the variable
                    // on each evaluation and not an all-pairs comparison.
                    // Every assert holds, as in a valid document, so that the output is the fired rules (as
                    // in SVRL) and not one failure per test: the tests are evaluated all the same.
                    var test = a == 0 ? "not(@ref) or @ref = head($ids) or exists($ids)" : string.Create(CultureInfo.InvariantCulture, $"not(@a) or @a != 'x{a}'");
                    sb.Append("    <xsl:choose><xsl:when test=\"").Append(test).Append("\"/><xsl:otherwise><failed-assert test=\"t").Append(a)
                        .Append("\" location=\"{local-name()}\"><text>assert ").Append(m).Append('-').Append(k).Append('-').Append(a)
                        .AppendLine("</text></failed-assert></xsl:otherwise></xsl:choose>");
                }
                sb.AppendLine(CultureInfo.InvariantCulture, $"""    <xsl:apply-templates select="*|comment()|processing-instruction()" mode="m{m}"/>""");
                sb.AppendLine("""  </xsl:template>""");
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"""  <xsl:template match="text()" mode="m{m}" priority="-1"/>""");
            sb.AppendLine(CultureInfo.InvariantCulture, $"""  <xsl:template match="@*|node()" mode="m{m}" priority="-2"><xsl:apply-templates select="*|comment()|processing-instruction()" mode="m{m}"/></xsl:template>""");
        }
        sb.AppendLine("</xsl:stylesheet>");
        return sb.ToString();
    }

    public static string Input(int rules, int targetBytes, int seed = 42)
    {
        var random = new Random(seed);
        var names = Math.Max(2, rules * 3 / 2);
        var sb = new StringBuilder(targetBytes + 1024);
        var id = 0;
        sb.Append("<doc>");
        while (sb.Length < targetBytes)
            AppendElement(sb, random, names, depth: 1, maxDepth: 8 + random.Next(5), ref id);
        sb.Append("</doc>");
        return sb.ToString();
    }

    private static void AppendElement(StringBuilder sb, Random random, int names, int depth, int maxDepth, ref int id)
    {
        var name = $"e{random.Next(names)}";
        sb.Append('<').Append(name).Append(" id=\"i").Append(id++).Append('"');
        if (random.Next(4) != 0)
            sb.Append(" a=\"v").Append(random.Next(1000)).Append('"');
        if (random.Next(3) == 0)
            sb.Append(" ref=\"i").Append(random.Next(Math.Max(1, id))).Append('"');
        sb.Append('>');
        if (depth < maxDepth)
        {
            var children = 1 + random.Next(4);
            for (var c = 0; c < children; c++)
                AppendElement(sb, random, names, depth + 1, maxDepth, ref id);
        }
        else
        {
            sb.Append("text ").Append(random.Next(100000));
        }
        sb.Append("</").Append(name).Append('>');
    }
}
