using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// root() with no argument uses the XPath context item, so `$n/root()` is the root of $n, not of
/// the XSLT current node; and a snapshot of an attribute is that attribute on the copied parent,
/// which has no children.
/// </summary>
public sealed class SnapshotRootTests
{
    private static async Task<string> RunAsync(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
            "<xsl:template match='/'><out>" + body + "</out></xsl:template></xsl:stylesheet>");
        return await t.TransformAsync("<r><e a='1'><c/></e></r>");
    }

    [Fact]
    public async Task Root_in_a_path_step_is_the_root_of_that_node()
        => (await RunAsync("<xsl:value-of select=\"snapshot(//e)/root() is /, root(snapshot(//e)) is /\"/>"))
            .Should().Contain("<out>false false</out>");

    [Fact]
    public async Task A_snapshot_of_an_attribute_is_the_attribute_on_a_childless_parent_copy()
        => (await RunAsync("<xsl:variable name='s' select='snapshot(//e/@a)'/>" +
                           "<xsl:value-of select=\"name($s), $s/.. ! (name(), count(node()), count(@*), @a is $s)\"/>"))
            .Should().Contain("<out>a e 0 1 true</out>");
}
