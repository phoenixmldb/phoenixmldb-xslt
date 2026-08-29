using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The engine initializes every global variable eagerly, in dependency order, before running
/// anything. XSLT 3.0 §2.3.2 (Priming a Stylesheet) says a dynamic error from a global whose
/// initializer depends on an absent context item may be raised during priming OR at the point of
/// reference, and need not be raised at all if the variable is never referenced. Priming eagerly
/// is a legal reading, but it turns an unevaluatable declaration in an *imported* module into a
/// fatal error for stylesheets that never look at it — and it is not the reading Saxon takes, so
/// real stylesheets depend on the other one.
///
/// The shape that matters in practice: a module declaring <c>&lt;xsl:variable select="/"/&gt;</c>
/// imported by a stylesheet invoked with an initial template and no source document. That is
/// exactly what XSpec generates — its test stylesheet imports the stylesheet under test and runs
/// it via a named template — so any suite whose subject declared such a global failed at load
/// time with XPDY0002, before a single assertion ran.
///
/// Deferring must keep the error attached to the variable. An earlier attempt skipped the failing
/// global outright; a later reference then reported <c>XPST0008 "not defined"</c>, which names the
/// wrong problem entirely — the variable is declared, its initializer failed.
/// </summary>
public class DeferredGlobalErrorTests
{
    private static async Task<string> Run(string stylesheet)
    {
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        t.SetInitialTemplate("go");
        return await t.TransformAsync((string?)null);
    }

    /// <summary>A global needing a context item, never referenced, must not fail the transform.</summary>
    [Fact]
    public async Task UnreferencedGlobal_RequiringAContextItem_DoesNotFailTheTransform()
    {
        var result = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:variable name="initial-document" as="document-node()" select="/"/>
              <xsl:template name="go"><out>ran</out></xsl:template>
            </xsl:stylesheet>
            """);
        result.Should().Contain("<out>ran</out>");
    }

    /// <summary>
    /// The XSpec shape: a second global whose select reads the deferred one. Its own evaluation
    /// fails too, so it must be deferred in turn rather than escaping — and in particular the
    /// deferred value must never leak into XQuery as an item ("context item is not a node: got
    /// item of type LazyValue"), which is what happened while deferred globals were still being
    /// bulk-bound into the XQuery scope.
    /// </summary>
    [Fact]
    public async Task GlobalReadingADeferredGlobal_IsItselfDeferred()
    {
        var result = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:variable name="initial-document" as="document-node()" select="/"/>
              <xsl:param name="is-external" as="xs:boolean"
                         select="$initial-document/description/@run-as = 'external'"/>
              <xsl:template name="go"><out>ran</out></xsl:template>
            </xsl:stylesheet>
            """);
        // The xs prefix is in scope and not excluded, so it is copied onto the literal result
        // element — match on the content rather than the exact start tag.
        result.Should().Contain(">ran</out>");
    }

    /// <summary>
    /// Deferring is not swallowing. A global that IS referenced must report the error its
    /// initializer actually raised, at the point of reference.
    /// </summary>
    [Fact]
    public async Task ReferencedGlobal_ReportsTheRealError_NotVariableNotDefined()
    {
        var act = async () => await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:variable name="initial-document" as="document-node()" select="/"/>
              <xsl:template name="go"><out><xsl:value-of select="count($initial-document)"/></out></xsl:template>
            </xsl:stylesheet>
            """);
        var ex = await act.Should().ThrowAsync<Exception>();
        // The real diagnostic is the absent context item, not "the variable does not exist".
        ex.Which.Message.Should().NotContain("XPST0008");
        ex.Which.Message.Should().NotContain("not defined");
        ex.Which.Message.Should().NotContain("not bound");
    }
}
