using System.Reflection;
using AwesomeAssertions;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The built-in template rule passes shared empty sort and with-param lists (one per process, not
/// one per node). Those lists must stay empty: a write would reach every later transform. Each read
/// of them checks that, so a write fails at the next built-in rule.
/// </summary>
[Collection(nameof(BuiltInRuleSharedListSerial))]
public class BuiltInRuleSharedListTests
{
    private static async Task<string> TransformAsync(string stylesheet, string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(input);
    }

    private static List<T> Shared<T>(string field) =>
        (List<T>)typeof(DefaultXsltExecutionContext).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    // The built-in rule between template rules that sort, pass parameters, apply-imports and next-match.
    private const string Stylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:template match="/">
            <out><xsl:apply-templates><xsl:with-param name="p" select="'x'" tunnel="yes"/></xsl:apply-templates></out>
          </xsl:template>
          <xsl:template match="list">
            <xsl:apply-templates select="i"><xsl:sort select="." order="descending"/></xsl:apply-templates>
          </xsl:template>
          <xsl:template match="i" priority="2"><xsl:next-match/></xsl:template>
          <xsl:template match="i"><xsl:param name="p" tunnel="yes"/>[<xsl:value-of select="., $p"/>]</xsl:template>
        </xsl:stylesheet>
        """;

    private const string Input = "<r><wrap><list><i>1</i><i>3</i><i>2</i></list></wrap><t>text</t></r>";

    [Fact]
    public async Task SharedLists_StayEmpty_AfterTransformsThatUseTheBuiltInRule()
    {
        for (var run = 0; run < 3; run++)
        {
            var result = await TransformAsync(Stylesheet, Input);
            result.Should().Be("<out>[3 x][2 x][1 x]text</out>");
        }

        Shared<XsltSort>("s_noSorts").Should().BeEmpty();
        Shared<XsltWithParam>("s_noWithParams").Should().BeEmpty();
    }

    [Fact]
    public async Task AWrittenSharedList_FailsAtTheNextBuiltInRule()
    {
        var noSorts = Shared<XsltSort>("s_noSorts");
        noSorts.Add(new XsltSort());
        try
        {
            var act = () => TransformAsync(Stylesheet, Input);
            (await act.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain("shared empty sort or with-param list");
        }
        finally
        {
            noSorts.Clear();
        }
        // Clean again: the next transform works.
        (await TransformAsync(Stylesheet, Input)).Should().Be("<out>[3 x][2 x][1 x]text</out>");
    }
}

[CollectionDefinition(nameof(BuiltInRuleSharedListSerial), DisableParallelization = true)]
public sealed class BuiltInRuleSharedListSerial;
