using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public class StreamingForEachIntermediatePredicateTests
{
    private static async Task<string> Run(string stylesheet, string inputXml, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"streaming-fe-imd-{Guid.NewGuid():N}");
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

    // Two departments, each with two employees; employee has empnum text + @name.
    private const string Works = """
        <works>
          <department name="sales">
            <employee name="Jane"><empnum>E1</empnum></employee>
            <employee name="John"><empnum>E2</empnum></employee>
          </department>
          <department name="dev">
            <employee name="Amy"><empnum>E3</empnum></employee>
            <employee name="Bob"><empnum>E4</empnum></employee>
          </department>
        </works>
        """;

    private static string Sheet(string select, string body) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:mode streamable="yes"/>
          <xsl:template name="xsl:initial-template">
            <xsl:source-document streamable="yes" href="w.xml">
              <out><xsl:for-each select="{{select}}">{{body}}</xsl:for-each></out>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    // Intermediate [1] on employee + text() tail: only each department's FIRST employee's empnum text.
    [Fact]
    public async Task IntermediatePositional_First_TextTail()
    {
        // Context item is the empnum text node: .. = empnum, ../.. = employee, so
        // ../../@name is the EMPLOYEE's @name (Jane/Amy), per the non-streaming oracle.
        var r = await Run(Sheet("works/department/employee[1]/empnum/text()",
            """<e v="{.}" d="{../../@name}"/>"""), Works, "w.xml");
        r.Trim().Should().Be("""<out><e v="E1" d="Jane"/><e v="E3" d="Amy"/></out>""");
    }

    // [2] variant: only the SECOND employee per department — proves position honored.
    [Fact]
    public async Task IntermediatePositional_Second_TextTail()
    {
        // ../../@name = the EMPLOYEE's @name (see First_TextTail). The [2] selects each
        // department's SECOND employee per-parent (John/Bob), proving position is honored.
        var r = await Run(Sheet("works/department/employee[2]/empnum/text()",
            """<e v="{.}" d="{../../@name}"/>"""), Works, "w.xml");
        r.Trim().Should().Be("""<out><e v="E2" d="John"/><e v="E4" d="Bob"/></out>""");
    }

    // Element tail (M1 only, no text enumeration): employee[1]/empnum element.
    [Fact]
    public async Task IntermediatePositional_ElementTail()
    {
        var r = await Run(Sheet("works/department/employee[1]/empnum",
            """<e v="{.}"/>"""), Works, "w.xml");
        r.Trim().Should().Be("""<out><e v="E1"/><e v="E3"/></out>""");
    }

    // Motionless intermediate predicate: only the 'sales' department's employees.
    [Fact]
    public async Task IntermediateMotionless_AttributeFilter()
    {
        var r = await Run(Sheet("works/department[@name='sales']/employee/empnum/text()",
            """<e v="{.}"/>"""), Works, "w.xml");
        r.Trim().Should().Be("""<out><e v="E1"/><e v="E2"/></out>""");
    }

    // NEGATIVE: a last()-bearing intermediate predicate is NOT streamed unsoundly and is
    // NOT silently dropped. last() is not forward-countable, so the intermediate predicate
    // is never captured here. Rather than dispatch the unfiltered (all-four-employees)
    // result a silent-drop would yield, the streamability analysis rejects the whole
    // for-each up front with XTSE3430 (last() needs the total count) — the soundest
    // possible outcome. The key invariant the negative guards: it must NEVER produce
    // <out><e v="E1"/><e v="E2"/><e v="E3"/><e v="E4"/></out> by silently dropping the
    // predicate. (Verified against the pre-change engine: XTSE3430 is the existing
    // behavior, unchanged by this slice's intermediate capture, which only admits
    // forward-countable-positional / motionless predicates.)
    [Fact]
    public async Task IntermediateLast_NotSilentlyDropped()
    {
        Func<Task> act = () => Run(Sheet("works/department/employee[last()]/empnum/text()",
            """<e v="{.}"/>"""), Works, "w.xml");
        (await act.Should().ThrowAsync<Engine.XsltException>())
            .Which.Message.Should().Contain("last()");
    }
}
