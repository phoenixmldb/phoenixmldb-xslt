using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The same defect as <see cref="PatternNestedNameTestNamespaceTests"/>, one level further out.
/// That one found a NameTest nested inside a KindTest; this one finds whole patterns nested
/// inside WRAPPER patterns.
///
/// <c>ResolveNamespacesInPattern</c> walked PathPattern, UnionPattern, ExceptPattern and
/// IntersectPattern, and stopped. Five subclasses wrap another pattern and were never visited:
/// ParenthesizedPositionalPattern (<c>Inner</c>) and the <c>Continuation</c> of KeyPattern,
/// IdPattern, VariableReferencePattern and DocFunctionPattern. The wrapped pattern kept its
/// prefixes unresolved, so its NameTest compared an unresolved NamespaceId and matched nothing.
///
/// The failure is silent and asymmetric, which is why it survived this long:
///
///   match="(x:a | x:b)[true()]"        matched NOTHING
///   match="x:a[true()] | x:b[true()]"  worked        (no wrapper - UnionPattern is visited)
///   match="(a | b)[true()]"            worked        (no prefix to resolve)
///
/// The obvious test for parenthesized patterns uses no namespace prefix and passes.
///
/// Found via XSpec, whose stacked-vardecls accumulator is
/// <c>(x:scenario/x:param | x:scenario/x:variable | x:context)[...]</c>. It silently never
/// fired, so no variable was ever pushed, so every compiled x:expect template was generated
/// without the params for the variables in scope — and the suite died at run time with
/// "Variable $myv:after_call not bound", naming a variable the user really had declared.
/// </summary>
public class PatternWrappedSubpatternNamespaceTests
{
    private const string Ns = "urn:test:wrapped";

    private static async Task<string> Run(string stylesheetBody, string input)
    {
        var xslt = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:x="{Ns}" xmlns:other="urn:test:other" exclude-result-prefixes="#all">
              {stylesheetBody}
            </xsl:stylesheet>
            """;
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xslt);
        return await t.TransformAsync(input);
    }

    private const string Doc = $"<r xmlns=\"{Ns}\"><a/><b/><c/></r>";

    private static Task<string> MatchWith(string pattern) => Run($"""
        <xsl:template match="/*"><out><xsl:apply-templates select="*"/></out></xsl:template>
        <xsl:template match="{pattern}"><HIT><xsl:value-of select="local-name()"/></HIT></xsl:template>
        <xsl:template match="*"><MISS><xsl:value-of select="local-name()"/></MISS></xsl:template>
        """, Doc);

    [Fact]
    public async Task ParenthesizedUnionWithPredicate_ResolvesPrefixesInsideTheParentheses()
    {
        var result = await MatchWith("(x:a | x:b)[true()]");
        result.Should().Contain("<HIT>a</HIT>").And.Contain("<HIT>b</HIT>");
        result.Should().Contain("<MISS>c</MISS>", "only a and b are named in the pattern");
    }

    [Fact]
    public async Task ParenthesizedSingleStepWithPredicate_ResolvesItsPrefix()
    {
        var result = await MatchWith("(x:a)[true()]");
        result.Should().Contain("<HIT>a</HIT>");
        result.Should().Contain("<MISS>b</MISS>").And.Contain("<MISS>c</MISS>");
    }

    /// <summary>
    /// The control that always passed. Without a wrapper the UnionPattern arm resolves the
    /// prefixes, so this shape worked throughout and made the bug look like it was about
    /// predicates rather than about parentheses.
    /// </summary>
    [Fact]
    public async Task UnparenthesizedUnionWithPredicates_StillWorks()
    {
        var result = await MatchWith("x:a[true()] | x:b[true()]");
        result.Should().Contain("<HIT>a</HIT>").And.Contain("<HIT>b</HIT>");
        result.Should().Contain("<MISS>c</MISS>");
    }

    /// <summary>
    /// A wrong prefix inside the parentheses must still match nothing. A "fix" that skipped
    /// resolution altogether — leaving every name test to match on local name — would pass the
    /// positive cases above and fail here.
    /// </summary>
    [Fact]
    public async Task WrongPrefixInsideParentheses_MatchesNothing()
    {
        var result = await MatchWith("(other:a | other:b)[true()]");
        result.Should().NotContain("<HIT");
        result.Should().Contain("<MISS>a</MISS>").And.Contain("<MISS>b</MISS>");
    }

    /// <summary>
    /// The accumulator case this was found through: a rule whose match pattern is a
    /// parenthesized union of prefixed names. It never fired, and because an accumulator that
    /// never fires just keeps its initial value, the symptom appeared far away — as a variable
    /// reference that would not bind.
    /// </summary>
    [Fact]
    public async Task AccumulatorRule_WithParenthesizedPrefixedPattern_Fires()
    {
        var result = await Run("""
            <xsl:accumulator name="stacked" as="element()*" initial-value="()">
              <xsl:accumulator-rule match="(x:a | x:b)[true()]" select="$value, ."/>
            </xsl:accumulator>
            <xsl:mode use-accumulators="#all"/>
            <xsl:template match="/*"><out><xsl:apply-templates select="x:c"/></out></xsl:template>
            <xsl:template match="x:c"><n><xsl:value-of select="count(accumulator-before('stacked'))"/></n></xsl:template>
            """, Doc);
        result.Should().Contain("<n>2</n>", "both x:a and x:b precede x:c and must have been pushed");
    }
}
