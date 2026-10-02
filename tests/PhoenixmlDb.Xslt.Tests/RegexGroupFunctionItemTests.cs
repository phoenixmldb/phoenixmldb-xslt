using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A function item for regex-group has no current captured substrings (XSLT 3.0 §5.3.4), so it
/// returns "" even when called inside xsl:analyze-string; a direct call still sees the groups.
/// </summary>
public sealed class RegexGroupFunctionItemTests
{
    [Fact]
    public async Task A_function_item_returns_empty_and_a_direct_call_the_group()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
            "<xsl:template name='xsl:initial-template'><out><xsl:variable name='g' select='regex-group#1'/>" +
            "<xsl:analyze-string select=\"'ab'\" regex='(a)(b)'><xsl:matching-substring>" +
            "[<xsl:value-of select='$g(2)'/>|<xsl:value-of select=\"function-lookup(xs:QName('fn:regex-group'), 1)(2)\" xmlns:xs='http://www.w3.org/2001/XMLSchema'/>|<xsl:value-of select='regex-group(2)'/>]" +
            "</xsl:matching-substring></xsl:analyze-string></out></xsl:template></xsl:stylesheet>");
        (await t.TransformAsync((string?)null)).Should().Contain("<out>[||b]</out>");
    }
}
