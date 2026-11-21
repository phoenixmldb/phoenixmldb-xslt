using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The functions a use-when expression can call are those of XSLT 3.0 §9.7's static context
/// (xslt BUGS.md #132): "The functions defined in [Functions and Operators 3.0] in the fn and math
/// namespaces, together with: the functions element-available, function-available,
/// type-available, available-system-properties, and system-property defined in this
/// specification; ... constructor functions for built-in types; the set of extension functions
/// that are present in the static context of every XPath expression ..." Stylesheet functions and
/// functions in other namespaces are not there, so a call to one is XPST0017.
/// </summary>
public class UseWhenStaticContextTests
{
    private static async Task<string> RunAsync(string useWhen, string top = "")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(
            "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform' xmlns:xs='http://www.w3.org/2001/XMLSchema' xmlns:o='urn:other'>" + top +
            "<xsl:template name='xsl:initial-template'><out><yes xsl:use-when=\"" + useWhen + "\"/></out></xsl:template></xsl:stylesheet>");
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("function-available('concat')")]
    [InlineData("element-available('xsl:for-each')")]
    [InlineData("type-available('xs:integer')")]
    [InlineData("system-property('xsl:version') = '3.0'")]
    [InlineData("exists(available-system-properties())")]
    [InlineData("not(function-available('o:true'))")]
    [InlineData("$flag", "<xsl:param name='flag' static='yes' select='true()'/>")]
    [InlineData("$n = 3", "<xsl:variable name='n' static='yes' select='1 + 2'/>")]
    public async Task Functions_of_the_static_context_work(string useWhen, string top = "")
    {
        (await RunAsync(useWhen, top)).Should().Contain("<yes/>");
    }

    [Theory]
    [InlineData("o:true()")]
    [InlineData("Q{urn:other}true()")]
    public async Task A_function_in_another_namespace_is_XPST0017(string useWhen)
    {
        var act = () => RunAsync(useWhen);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XPST0017");
    }
}
