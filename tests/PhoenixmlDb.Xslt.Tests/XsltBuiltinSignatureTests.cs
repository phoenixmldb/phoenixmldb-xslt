using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The XSLT library's built-in functions declare the parameter cardinalities of the XSLT 3.0 and
/// F&amp;O 3.1 signatures. Several were declared exactly-one or <c>xs:string?</c> where the spec
/// admits more. Invocation never checked, so calls worked, but the declared signature is what a
/// function item carries: <c>document#1</c> was not an instance of <c>function(item()*) as
/// item()*</c>. A cardinality check at the call site would turn each of them into a rejected
/// valid call — <c>document(/xxx/ref/@file)</c> over two attributes raised XPTY0004 (W3C
/// document-0105 and 18 more, bug-2701).
/// </summary>
public sealed class XsltBuiltinSignatureTests
{
    private static async Task<string> EvalAsync(string xpath)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:value-of select="{xpath}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    [Theory]
    [InlineData("document#1 instance of function(item()*) as item()*")]
    [InlineData("document#2 instance of function(item()*, node()) as item()*")]
    [InlineData("parse-json#1 instance of function(xs:string?) as item()?")]
    [InlineData("json-to-xml#1 instance of function(xs:string?) as item()?")]
    [InlineData("stream-available#1 instance of function(xs:string?) as xs:boolean")]
    public async Task ABuiltin_IsAnInstanceOfItsSpecSignature(string test)
        => (await EvalAsync(test)).Should().Be("true");

    [Theory]
    [InlineData("parse-json(())", "")]
    [InlineData("json-to-xml(())", "")]
    [InlineData("format-number((), '0')", "NaN")]
    [InlineData("stream-available(())", "false")]
    public async Task AnEmptyArgumentTheSpecAllows_IsAccepted(string call, string expected)
        => (await EvalAsync(call)).Should().Be(expected);
}
