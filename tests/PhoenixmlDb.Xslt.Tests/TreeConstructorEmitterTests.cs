using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// SP-B temp-tree node-model migration: pins for the from-scratch emitters that build
/// XDM nodes directly via <c>TreeConstructor</c> (behind the <c>PXDB_TEMPTREE_DIFF</c>
/// differential in slice 1). Each test asserts the observable temp-tree behavior through
/// the public transform surface, so it holds whether the value came from the legacy
/// serialize-reparse path (production, toggle off) or the node-build path.
/// </summary>
public class TreeConstructorEmitterTests
{
    private static async Task<string> TransformAsync(string stylesheet, string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(input);
    }

    [Fact]
    public async Task XslElement_SimpleContent_BuildsElementNodeWithAttributeAndText()
    {
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/">
                <xsl:variable name="v" as="element()">
                  <xsl:element name="a"><xsl:attribute name="k">1</xsl:attribute>hi</xsl:element>
                </xsl:variable>
                <out>{name($v)}|{$v/@k}|{string($v)}</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, "<r/>");
        result.Should().Contain("<out>a|1|hi</out>");
    }

    [Fact]
    public async Task Lre_NestedWithNamespace_BuildsNodeTree()
    {
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/">
                <xsl:variable name="v" as="element()">
                  <a xmlns:p="urn:p"><p:b>x</p:b></a>
                </xsl:variable>
                <out>{$v/*/local-name()}|{namespace-uri($v/*)}</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, "<r/>");
        result.Should().Contain("<out>b|urn:p</out>");
    }

    [Fact]
    public async Task XslCopy_ElementWithCopiedAttributes_BuildsNodeTree()
    {
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/">
                <xsl:variable name="v" as="element()">
                  <xsl:for-each select="a"><xsl:copy><xsl:copy-of select="@*"/></xsl:copy></xsl:for-each>
                </xsl:variable>
                <out>{name($v)}|{$v/@k}</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, """<a k="7"/>""");
        result.Should().Contain("<out>a|7</out>");
    }

    [Fact]
    public async Task NestedApplyTemplatesOverAtomics_BuildsChildElementsNatively()
    {
        // A temp tree whose nested elements come from apply-templates over an atomic sequence
        // (text-value-templates supplying the leaf text) must build the full element tree in
        // document order: csv → 2 rows → 2 fields each, each field carrying its own text.
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" expand-text="yes">
              <xsl:mode name="pf" on-no-match="shallow-copy"/>
              <xsl:template match="/">
                <xsl:variable name="result" as="element()">
                  <csv>
                    <xsl:apply-templates select="tokenize('a,b|c,d','\|')" mode="pl"/>
                  </csv>
                </xsl:variable>
                <out>{count($result/row)}|{count($result/row[1]/field)}|{string($result/row[2]/field[2])}</out>
              </xsl:template>
              <xsl:template match="." mode="pl">
                <row><xsl:apply-templates select="tokenize(.,',')" mode="pf"/></row>
              </xsl:template>
              <xsl:template match="." mode="pf">
                <field>{.}</field>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, "<r/>");
        result.Should().Contain(">2|2|d</out>");
    }

    [Fact]
    public async Task MixedContent_TextElementCommentPi_PreservesDocumentOrder()
    {
        // slice 4: text interleaved with a child element, then a comment and a PI, must land as
        // five child nodes in document order (t1, b, t2, comment, pi) — the same whether the
        // value came from the reparse path (production) or the native node-build (differential).
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/"><xsl:variable name="v" as="element()">
                <a>t1<b/>t2<xsl:comment>c</xsl:comment><xsl:processing-instruction name="pi">d</xsl:processing-instruction></a>
              </xsl:variable><out>{count($v/node())}</out></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, "<r/>");
        result.Should().Contain("<out>5</out>"); // t1, b, t2, comment, pi
    }

    [Fact]
    public async Task FullyMigratedTempTree_ProductionNodePath_RoundTripsIdentically()
    {
        // slice 5a regression pin: a fully-migrated temp tree (LRE + attribute + text + nested
        // element, no xsl:copy-of / built-in copy / accumulator items) is now DELIVERED from the
        // native node build in PRODUCTION (toggle off). Assert the delivered tree's shape and
        // values through the public surface — byte-parity with the legacy reparse (zero behavior
        // change). If the flip ever diverges, the counts/values below break.
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/">
                <xsl:variable name="v" as="element()">
                  <a k="1"><b>hi</b><c/></a>
                </xsl:variable>
                <out>{name($v)}|{$v/@k}|{name($v/*[1])}|{string($v/b)}|{count($v/*)}|{$v/self::a and true()}</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, "<r/>");
        result.Should().Contain("<out>a|1|b|hi|2|true</out>");
    }

    [Fact]
    public async Task MigratedTempTree_SerializedOutput_ByteParity()
    {
        // slice 5a: the delivered node tree must serialize byte-identically when copied to
        // final output, including attribute and nested-element markup.
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no"/>
              <xsl:template match="/">
                <xsl:variable name="v" as="element()">
                  <a k="1"><b>hi</b></a>
                </xsl:variable>
                <xsl:copy-of select="$v"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, "<r/>");
        result.Should().Contain("""<a k="1"><b>hi</b></a>""");
    }

    [Fact]
    public async Task PureTextTypedBody_ParentlessTextNode_HasEmptyBaseUri()
    {
        // SP-B 5a byte-parity: a pure-text (no-'<') as="text()" body must NOT flip to the native
        // node root. That path (AddTextChunk) creates a parentless XdmText with BaseUri left null,
        // so base-uri($v) is (). Flipping would apply the markup base-URI fixup (seqBaseUri) and
        // regress base-uri to the stylesheet URI. The differential skips this class (no '<'), so it
        // is never proven byte-equal — hence it must stay on the reparse path.
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/">
                <xsl:variable name="v" as="text()">hello</xsl:variable>
                <out>[{base-uri($v)}]</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // A non-null stylesheet base URI is required to expose the regression: the buggy flip
        // sets the text node's BaseUri = seqBaseUri (the stylesheet URI), so base-uri($v) would
        // return that URI instead of ().
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(xslt, new System.Uri("http://example.com/test.xsl"));
        var result = await transformer.TransformAsync("<r/>");
        result.Should().Contain("<out>[]</out>");
    }

    [Fact]
    public async Task XslCopy_OfAlreadyCopiedElement_PreservesCopySourceBaseUri()
    {
        // SP-B whole-branch review finding #1 (byte-parity gap). A migrated temp-tree body that
        // xsl:copies a source element which is ITSELF a prior copy carrying CopySourceBaseUri.
        //   $c  : orphan copy of source <e> (xml:base gives it CopySourceBaseUri = http://ex/base/)
        //   $c2 : copy of $c inside a fully-migrated body -> hits the xsl:copy element-shell flip.
        // At the flip site the constructor must reproduce the base sentinel's value
        //   elem.CopySourceBaseUri ?? ComputeSourceBaseUri(elem)
        // For $c: ComputeSourceBaseUri($c) is () (orphan, no xml:base) but $c.CopySourceBaseUri is
        // http://ex/base/. Pre-fix the flip passed plain ComputeSourceBaseUri(elem) -> base-uri($c2)
        // regressed to (). Post-fix (and on the legacy reparse path) base-uri($c2) = http://ex/base/.
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/">
                <xsl:variable name="c" as="element()">
                  <xsl:for-each select="/doc/e"><xsl:copy/></xsl:for-each>
                </xsl:variable>
                <xsl:variable name="c2" as="element()">
                  <xsl:for-each select="$c"><xsl:copy/></xsl:for-each>
                </xsl:variable>
                <out>[{base-uri($c2)}]</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, """<doc xml:base="http://ex/base/"><e/></doc>""");
        result.Should().Contain("<out>[http://ex/base/]</out>");
    }

    [Fact]
    public async Task CopyOf_SourceElement_InMigratedBody_RoundTrips()
    {
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/"><xsl:variable name="v" as="element()*">
                <xsl:copy-of select="a"/>
              </xsl:variable><out>{count($v)}|{$v/@k}|{local-name($v)}</out></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, """<a k="7"/>""");
        result.Should().Contain("<out>1|7|a</out>");
    }

    [Fact]
    public async Task CopyOf_NestedInsideXslCopy_OfSourceElement_RoundTrips()
    {
        // SP-C slice 2 pin (the nested-copy divergence). A migrated temp-tree body whose xsl:copy
        // of a source element CONTAINS an inner xsl:copy-of of another source element — the shape
        // f:graft-to-parent (fn/snapshot) builds. Lifting Task 1's tc.Depth==0 restriction lets
        // this nested copy-of route into the constructor and the enclosing xsl:copy body flip.
        // Two byte-parity gaps that surfaced, pinned here against the production reparse:
        //   (1) double text-append: the copied <inner>'s own text ("X") was ALSO routed into the
        //       enclosing frame as a spurious extra child -> count($p/node())==2, string($p)=="XX".
        //   (2) base-uri: the copied <inner> must carry the source base URI (xml:base), so the
        //       copy's base-uri() resolves to it rather than ().
        // Correct (legacy reparse AND fixed node build): one child, string "X", base http://ex/base/.
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/">
                <xsl:variable name="p" as="element()">
                  <xsl:copy select="/doc/outer">
                    <xsl:copy-of select="/doc/outer/inner"/>
                  </xsl:copy>
                </xsl:variable>
                <out>[{count($p/node())}|{string($p)}|{base-uri($p/inner)}]</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, """<doc xml:base="http://ex/base/"><outer><inner>X</inner></outer></doc>""");
        result.Should().Contain("<out>[1|X|http://ex/base/]</out>");
    }

    [Fact]
    public async Task CopyOf_SubtreeWithNestedXmlBaseDescendant_InFlippingBody_DescendantBaseUriMatchesReparse()
    {
        // SP-C Task 2 follow-up (Important finding VERIFY). Task 2 stamps CopySourceBaseUri only on
        // the TOP-LEVEL copied roots (tcCopyElems). A copied SUBTREE whose DESCENDANT carries its
        // own xml:base is the untested case: that clone descendant keeps CopySourceBaseUri=null.
        //   copy-of select="/doc/wrap/outer" -> tcCopyElems == [outer] (stamped)
        //   <inner xml:base="sub/"> is a clone DESCENDANT of outer -> NOT stamped (CopySourceBaseUri=null)
        // On the production reparse path, SerializeNode's per-element TryEmitBaseSentinel fires for
        // <inner> too and the reparse recovers CopySourceBaseUri=http://ex/base/sub/ onto it. The
        // flip path relies instead on base-uri() recomputing from the copied xml:base chain: <inner>'s
        // xml:base="sub/" resolves against base-uri(outer clone), and outer clone (a stamped top-level
        // root) carries CopySourceBaseUri=http://ex/base/, so base-uri($v/outer/inner)=http://ex/base/sub/.
        // This pins that the flip descendant's base-uri() equals the reparse's — proving the finding
        // benign (the xml:base attribute CloneSubtreeDeep copies fully drives the computation).
        // Enclosing xsl:copy of /doc/wrap flips (mirrors CopyOf_NestedInsideXslCopy_OfSourceElement).
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/">
                <xsl:variable name="v" as="element()">
                  <xsl:copy select="/doc/wrap">
                    <xsl:copy-of select="/doc/wrap/outer"/>
                  </xsl:copy>
                </xsl:variable>
                <out>[{base-uri($v/outer/inner)}]</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(xslt, new System.Uri("http://doc/stylesheet.xsl"));
        var result = await transformer.TransformAsync(
            """<doc xml:base="http://ex/base/"><wrap><outer><inner xml:base="sub/">X</inner></outer></wrap></doc>""");
        // xml:base="sub/" resolved against outer's inherited base http://ex/base/ = http://ex/base/sub/.
        result.Should().Contain("<out>[http://ex/base/sub/]</out>");
    }

    [Fact]
    public async Task CopyOf_SubtreeRedeclaringInScopeNamespace_BailsToLegacy_OutputUnchanged()
    {
        // SP-C Task 2 review (Minor guard pin). CloneDeclaresRedundantNamespace bails a body to the
        // legacy serialize-reparse path (no flip) when a copied subtree redeclares a prefix already
        // in scope at the copy point — the shape f:graft-to-parent (fn/snapshot) builds. Here xmlns:foo
        // is in scope on <wrap>, and the copied <sub> redeclares xmlns:foo="http://foo/" (redundant).
        // The native clone keeps the redundant declaration (copy-namespaces retains in-scope decls),
        // which would flip to a DIFFERENT namespace-declaration set than the reparse (which strips it);
        // the guard detects that and stays on legacy so production output is byte-identical. This pins
        // the guard so a future change that drops it (letting such a body flip) fails loudly.
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <xsl:variable name="v" as="element()">
                  <wrap xmlns:foo="http://foo/">
                    <xsl:copy-of select="/doc/sub"/>
                  </wrap>
                </xsl:variable>
                <xsl:copy-of select="$v"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt,
            """<doc><sub xmlns:foo="http://foo/"><foo:x/></sub></doc>""");
        // Redundant xmlns:foo on <sub> is stripped (already in scope on <wrap>); foo:x keeps its binding.
        result.Should().Contain("""<wrap xmlns:foo="http://foo/"><sub><foo:x/></sub></wrap>""");
    }

    [Fact]
    public async Task CopyOf_MixedSequenceIntoLreWithDefaultNs_CopyNamespacesNo_NoParentAcquiredNamespace()
    {
        // SP-C targeted (W3C copy-1221). An untyped xsl:variable body wraps a MIXED
        // element+text copy-of (wrapper/child::node() = <a/>, "mid", <a/>) in an LRE that
        // carries a default namespace (xmlns="http://out/"). With copy-namespaces="no" the
        // copied elements retain ONLY the namespaces their own name / attributes require; and
        // per XSLT 3.0 §11.7.2 an element does NOT acquire an in-scope namespace merely because
        // it is present on the enclosing constructor's element — so http://out/ must appear on
        // none of the copied elements. The serialize-reparse fallback gets this wrong (re-nests
        // the copy under the default ns and reports it); the node build is authoritative and this
        // pins it.
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="f" expand-text="yes">
              <xsl:variable name="fragment">
                <wrapper xmlns:w="http://w/">
                  <a xmlns:a="http://a/" a:att="A"><aa xmlns="http://aa/"/></a>
                  <xsl:text>mid</xsl:text>
                  <a xmlns="http://a/"><aa xmlns:aa="http://aa/"/></a>
                </wrapper>
              </xsl:variable>
              <xsl:template match="/">
                <xsl:variable name="result">
                  <doc xmlns="http://out/">
                    <xsl:copy-of select="$fragment/wrapper/child::node()" copy-namespaces="no"/>
                  </doc>
                </xsl:variable>
                <out>{f:ns($result/*/*[1])}|{f:ns($result/*/*[1]/*[1])}|{f:ns($result/*/*[2])}|{f:ns($result/*/*[2]/*[1])}</out>
              </xsl:template>
              <xsl:function name="f:ns" as="xs:string">
                <xsl:param name="e" as="element()"/>
                <xsl:sequence select="string-join(sort(in-scope-prefixes($e)[. != 'xml'] ! (. || '=' || namespace-uri-for-prefix(., $e))), ',')"/>
              </xsl:function>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, "<r/>");
        // a[0] needs only its a:att prefix; aa[0] only its default (http://aa/); a[1] and aa[1]
        // only the default (http://a/). None acquires http://out/ from the enclosing <doc>.
        result.Should().Contain(">a=http://a/|=http://aa/|=http://a/|=http://a/</out>");
        result.Should().NotContain("http://out/");
    }

    [Fact]
    public async Task UntypedVariable_LreAttributeTextNested_RoundTripsViaNodeBuild()
    {
        // SP-C slice 3: an untyped xsl:variable (no as=) is a temporary tree (document node).
        // Its body — an LRE with an attribute, text, and a nested element — is fully node-native,
        // so the flip delivers the constructor's document via CachedDocumentNode with no reparse.
        // Navigating it through several consumers must be byte-identical to the legacy reparse.
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/">
                <xsl:variable name="v">
                  <a k="1">hi<x>deep</x></a>
                </xsl:variable>
                <out>{name($v/a)}|{$v/a/@k}|{$v//x}|{count($v//*)}|{string($v)}</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, "<r/>");
        result.Should().Contain("<out>a|1|deep|2|hideep</out>");
    }

    [Fact]
    public void DifferentialCoverageCounters_AreResettableAndReadable()
    {
        // slice 5a observability: Compared/Skipped are exposed so a differential run can measure
        // how much of the corpus flips. They stay at 0 when the toggle is off (no production cost).
        PhoenixmlDb.Xslt.Engine.TempTreeDifferential.ResetCounters();
        PhoenixmlDb.Xslt.Engine.TempTreeDifferential.Compared.Should().Be(0);
        PhoenixmlDb.Xslt.Engine.TempTreeDifferential.Skipped.Should().Be(0);
    }
}
