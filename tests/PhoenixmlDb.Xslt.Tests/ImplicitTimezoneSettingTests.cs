using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The implicit timezone is a setting of the transformation
/// (<see cref="XsltTransformer.ImplicitTimezone"/>). It was the timezone of the machine, so the
/// same stylesheet on the same input gave different results on two machines.
/// </summary>
public class ImplicitTimezoneSettingTests
{
    private static readonly TimeSpan East = TimeSpan.FromHours(5);
    private static readonly TimeSpan West = TimeSpan.FromHours(-8);

    // Noon with no timezone is 07:00Z in the east and 20:00Z in the west.
    private const string Input = "<r><e at='2024-03-01T12:00:00'/><e at='2024-03-01T07:00:00Z'/><e at='2024-03-01T12:00:00Z'/></r>";

    private static string Sheet(string body, string declarations = "", string mode = "") => $"""
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema"
                        version="3.0" expand-text="yes" exclude-result-prefixes="xs">
          <xsl:output omit-xml-declaration="yes"/>
          {mode}
          {declarations}
          <xsl:template match="/"><out>{body}</out></xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<string> RunAsync(string stylesheet, TimeSpan? zone)
    {
        var t = new XsltTransformer { ImplicitTimezone = zone };
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(Input);
    }

    [Theory]
    // a comparison
    [InlineData("{xs:dateTime(r/e[1]/@at) eq xs:dateTime(r/e[2]/@at)}", "", "true", "false")]
    // xsl:sort
    [InlineData("<xsl:for-each select=\"r/e\"><xsl:sort select=\"xs:dateTime(@at)\"/>{position()}:{@at} </xsl:for-each>", "",
        "1:2024-03-01T12:00:00 2:2024-03-01T07:00:00Z 3:2024-03-01T12:00:00Z ",
        "1:2024-03-01T07:00:00Z 2:2024-03-01T12:00:00Z 3:2024-03-01T12:00:00 ")]
    // xsl:for-each-group with group-by
    [InlineData("<xsl:for-each-group select=\"r/e\" group-by=\"xs:dateTime(@at)\">{count(current-group())}</xsl:for-each-group>", "", "21", "111")]
    // xsl:for-each-group with group-adjacent
    [InlineData("<xsl:for-each-group select=\"r/e\" group-adjacent=\"xs:dateTime(@at)\">{count(current-group())}</xsl:for-each-group>", "", "21", "111")]
    // key()
    [InlineData("{count(key('at', xs:dateTime('2024-03-01T07:00:00Z')))}",
        "<xsl:key name=\"at\" match=\"e\" use=\"xs:dateTime(@at)\"/>", "2", "1")]
    [InlineData("{count(key('day', xs:date('2024-03-01+05:00')))}",
        "<xsl:key name=\"day\" match=\"e[1]\" use=\"xs:date('2024-03-01')\"/>", "1", "0")]
    // distinct-values, and the functions that tell the timezone
    [InlineData("{count(distinct-values(r/e/xs:dateTime(@at)))}", "", "2", "3")]
    [InlineData("{implicit-timezone()}", "", "PT5H", "-PT8H")]
    [InlineData("{timezone-from-dateTime(current-dateTime())}|{timezone-from-date(current-date())}|{timezone-from-time(current-time())}", "",
        "PT5H|PT5H|PT5H", "-PT8H|-PT8H|-PT8H")]
    // in a variable, a function and a template that is called
    [InlineData("<xsl:variable name=\"v\" select=\"xs:time('12:00:00') eq xs:time('07:00:00Z')\"/>{$v}", "", "true", "false")]
    [InlineData("{f:same()}", "<xsl:function name=\"f:same\" xmlns:f=\"urn:f\"><xsl:sequence select=\"xs:date('2024-03-01') eq xs:date('2024-03-01+05:00')\"/></xsl:function>", "true", "false")]
    public async Task The_setting_decides(string body, string declarations, string east, string west)
    {
        var sheet = Sheet(body, declarations).Replace("{f:same()}", "<xsl:value-of select=\"f:same()\" xmlns:f=\"urn:f\"/>", StringComparison.Ordinal);
        (await RunAsync(sheet, East)).Should().Be($"<out>{east}</out>");
        (await RunAsync(sheet, West)).Should().Be($"<out>{west}</out>");
    }

    [Fact]
    public async Task A_streamed_transformation_has_it_too()
    {
        var sheet = Sheet("<xsl:for-each select=\"r/e\">{xs:dateTime(@at) eq xs:dateTime('2024-03-01T07:00:00Z')} </xsl:for-each>{implicit-timezone()}",
            mode: "<xsl:mode streamable=\"yes\"/>");
        async Task<string> Streamed(TimeSpan zone)
        {
            var t = new XsltTransformer { ImplicitTimezone = zone };
            await t.LoadStylesheetAsync(sheet);
            using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Input));
            using var output = new StringWriter();
            await t.TransformAsync(input, output);
            return output.ToString();
        }
        (await Streamed(East)).Should().Be("<out>true true false PT5H</out>");
        (await Streamed(West)).Should().Be("<out>false true false -PT8H</out>");
        (await RunAsync(sheet, East)).Should().Be("<out>true true false PT5H</out>");
    }

    [Fact]
    public async Task Transformations_that_run_side_by_side_each_have_their_own()
    {
        var sheet = Sheet("<xsl:for-each select=\"1 to 200\">{xs:time('12:00:00') eq xs:time('07:00:00Z')}</xsl:for-each>{implicit-timezone()}");
        var runs = Enumerable.Range(0, 32).Select(i => Task.Run(async () => (i, await RunAsync(sheet, i % 2 == 0 ? East : West))));
        foreach (var (i, result) in await Task.WhenAll(runs))
        {
            var each = i % 2 == 0 ? "true" : "false";
            result.Should().Be($"<out>{string.Concat(Enumerable.Repeat(each, 200))}{(i % 2 == 0 ? "PT5H" : "-PT8H")}</out>");
        }
        PhoenixmlDb.Xdm.ImplicitTimezone.Configured.Should().BeNull("a transformation leaves nothing set for its caller");
    }

    [Fact]
    public async Task With_nothing_set_it_is_what_the_host_set_around_the_call_or_the_machine()
    {
        var sheet = Sheet("{implicit-timezone() eq xs:dayTimeDuration('PT5H')}");
        using (PhoenixmlDb.Xdm.ImplicitTimezone.Use(East))
        {
            (await RunAsync(sheet, null)).Should().Be("<out>true</out>");
        }
        var machine = DateTimeOffset.Now.Offset;
        (await RunAsync(sheet, null)).Should().Be($"<out>{(machine == East ? "true" : "false")}</out>");
    }

    [Fact]
    public async Task A_zone_outside_the_range_of_XML_Schema_is_refused()
    {
        var run = () => RunAsync(Sheet("x"), TimeSpan.FromHours(15));
        await run.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
