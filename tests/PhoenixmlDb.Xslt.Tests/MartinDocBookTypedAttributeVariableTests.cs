using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Martin Honnen 2026-08-12 (DocBook xslTNG 2.8.0 / 2.8.3, <c>modules/attributes.xsl:421</c>,
/// <c>XTDE0420: Cannot add an attribute node to a document node</c>). An <c>as=</c>-typed
/// sequence-constructor body constructs a SEQUENCE, not a temporary tree (XSLT 3.0 §9.3):
/// no document node wraps it, so attribute and namespace nodes are legal members of the
/// result. The engine left <c>_documentNodeDepth</c> at whatever the ENCLOSING construction
/// scope had, so an <c>xsl:attribute</c> inside <c>&lt;xsl:variable as="attribute()*"&gt;</c>
/// looked like an attempt to attach an attribute to a document node.
///
/// xslTNG hits this ~20 times over, via the idiom
/// <code>
///   &lt;xsl:variable name="attr" as="attribute()*"&gt;
///     &lt;xsl:apply-templates select="@*"/&gt;   &lt;!-- templates emit xsl:attribute --&gt;
///   &lt;/xsl:variable&gt;
/// </code>
/// and via <c>&lt;xsl:with-param as="attribute()*"&gt;</c> bodies (info.xsl:418/473), so the
/// variable, param, and with-param seams are all covered here.
///
/// Neither test layer caught this: the unit suite was green at 1196 and the W3C conformance
/// suite cannot fail on a regression (it asserts only "passed > 0").
/// </summary>
public sealed class MartinDocBookTypedAttributeVariableTests
{
    /// <summary>
    /// The reported shape. The outer untyped xsl:variable builds a temporary tree (raising
    /// the document depth); the inner typed variable must not inherit that guard.
    /// </summary>
    [Fact]
    public async Task TypedAttributeVariable_InsideDocumentScope_DoesNotRaiseXTDE0420()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="xml" indent="no"/>
              <xsl:template match="@xml:id"><xsl:attribute name="id" select="."/></xsl:template>
              <xsl:template match="/">
                <xsl:variable name="rtf">
                  <xsl:variable name="attr" as="attribute()*">
                    <xsl:apply-templates select="r/@*"/>
                  </xsl:variable>
                  <out><xsl:sequence select="$attr"/></out>
                </xsl:variable>
                <xsl:copy-of select="$rtf"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync("""<r xmlns:xml="http://www.w3.org/XML/1998/namespace" xml:id="a1"/>""");
        r.Should().Contain("""id="a1" """.TrimEnd());
    }

    [Fact]
    public async Task TypedAttributeParam_InsideDocumentScope_DoesNotRaiseXTDE0420()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="xml" indent="no"/>
              <xsl:template name="build">
                <xsl:param name="attr" as="attribute()*">
                  <xsl:attribute name="class" select="'default'"/>
                </xsl:param>
                <out><xsl:sequence select="$attr"/></out>
              </xsl:template>
              <xsl:template match="/">
                <xsl:variable name="rtf"><xsl:call-template name="build"/></xsl:variable>
                <xsl:copy-of select="$rtf"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync("<r/>");
        r.Should().Contain("""class="default" """.TrimEnd());
    }

    /// <summary>xslTNG info.xsl:418/473 — <c>xsl:with-param as="attribute()*"</c> with a body.</summary>
    [Fact]
    public async Task TypedAttributeWithParam_InsideDocumentScope_DoesNotRaiseXTDE0420()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="xml" indent="no"/>
              <xsl:template name="build">
                <xsl:param name="extra-attributes" as="attribute()*"/>
                <out><xsl:sequence select="$extra-attributes"/></out>
              </xsl:template>
              <xsl:template match="/">
                <xsl:variable name="rtf">
                  <xsl:call-template name="build">
                    <xsl:with-param name="extra-attributes" as="attribute()*">
                      <xsl:attribute name="role" select="'note'"/>
                    </xsl:with-param>
                  </xsl:call-template>
                </xsl:variable>
                <xsl:copy-of select="$rtf"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync("<r/>");
        r.Should().Contain("""role="note" """.TrimEnd());
    }

    /// <summary>
    /// The relaxation must not make an attribute at the top level of an
    /// <c>as="document-node()"</c> body legal — that body really is wrapped in a document
    /// node. Guards against "fixing" the bug by disarming the check everywhere.
    ///
    /// Note the engine reports this as <c>XTTE0570</c> (the constructed value does not match
    /// the declared type) rather than <c>XTDE0420</c>: at the top level there is no enclosing
    /// document scope to inherit, so the attribute is rejected by the type check on the
    /// result instead of by the construction guard. Asserting the code we actually emit
    /// rather than the one that would be more precise; both reject the stylesheet.
    /// </summary>
    [Fact]
    public async Task TypedDocumentNodeVariable_TopLevelAttribute_IsStillRejected()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template match="/">
                <xsl:variable name="d" as="document-node()">
                  <xsl:attribute name="oops" select="'x'"/>
                </xsl:variable>
                <xsl:sequence select="$d"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var act = async () => await t.TransformAsync("<r/>");
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTTE0570");
    }
}
