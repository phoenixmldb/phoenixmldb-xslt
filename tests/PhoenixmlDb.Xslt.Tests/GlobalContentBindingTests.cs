using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The engine had TWO implementations of "bind a global variable from a content sequence
/// constructor". The eager, dependency-ordered pass honoured the declared <c>as</c> type; the
/// lazy on-demand pass stored the serialized content as a raw xs:string and ignored <c>as</c>
/// entirely. So one declaration bound a document node or a string depending only on which pass
/// reached it first.
///
/// Which pass reached it depended on whether the dependency analysis spotted the reference — and
/// it does not recognise EQName (<c>$Q{uri}local</c>) references. So the TYPE of a variable
/// depended on how some other expression happened to spell its name.
///
/// The eager path already carried a fix for exactly this failure, from Martin Honnen's DocBook
/// report: "falling through to the RTF/string path produced an xs:string, which then failed
/// axis-step evaluation with XPTY0020". Fixed in one of the two paths. XSpec hit the other, with
/// the same error code, on 20 suites (census 09 -> 11: XPTY0020 21 -> 0).
/// </summary>
public class GlobalContentBindingTests
{
    private static async Task<string> Run(string stylesheet)
    {
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        t.SetInitialTemplate("go");
        return await t.TransformAsync((string?)null);
    }

    private static string Sheet(string varName, string varRef) => $$"""
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
          xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xsl:variable name="{{varName}}" as="document-node()"><xsl:document><foo>t</foo></xsl:document></xsl:variable>
          <xsl:param name="gp" select="${{varRef}}"/>
          <xsl:template name="go"><out isDoc="{$gp instance of document-node()}"
                                       isStr="{$gp instance of xs:string}"/></xsl:template>
        </xsl:stylesheet>
        """;

    /// <summary>
    /// The reported shape. A plain name is seen by the dependency analysis, so this always went
    /// down the eager path and always worked — which is why it hid the bug.
    /// </summary>
    [Fact]
    public async Task Plain_named_global_binds_its_declared_type()
        => (await Run(Sheet("dv", "dv"))).Should().Contain("isDoc=\"true\"").And.Contain("isStr=\"false\"");

    /// <summary>
    /// An EQName reference is NOT seen by the dependency analysis, so the consumer takes the lazy
    /// path. Before the fix this bound xs:string — the serialized document — and the first axis
    /// step on it raised XPTY0020.
    /// </summary>
    [Fact]
    public async Task EQName_named_global_binds_its_declared_type_too()
        => (await Run(Sheet("Q{urn:test:p}dv", "Q{urn:test:p}dv")))
            .Should().Contain("isDoc=\"true\"").And.Contain("isStr=\"false\"");

    /// <summary>An empty-URI EQName took the eager path; kept so the pair cannot drift apart.</summary>
    [Fact]
    public async Task Empty_uri_EQName_global_binds_its_declared_type()
        => (await Run(Sheet("Q{}dv", "Q{}dv"))).Should().Contain("isDoc=\"true\"");

    /// <summary>
    /// XSpec's actual generated shape: a document-node global consumed by a global param via
    /// simple map, which is where the axis step fired.
    /// </summary>
    [Fact]
    public async Task Global_param_can_step_into_an_EQName_document_global()
    {
        var xslt = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:variable name="Q{urn:x-xspec:compile:impl}doc" as="document-node()">
                <xsl:document><foo a="1">t</foo></xsl:document>
              </xsl:variable>
              <xsl:param name="Q{}gp" select="$Q{urn:x-xspec:compile:impl}doc ! ( node() )"/>
              <xsl:template name="go"><out n="{count($Q{}gp/self::foo)}"/></xsl:template>
            </xsl:stylesheet>
            """;
        (await Run(xslt)).Should().Contain("n=\"1\"");
    }
}
