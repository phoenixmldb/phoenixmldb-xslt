using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:copy-of of a parentless namespace node into a node()-typed variable yields a namespace
/// node. It was serialized into the buffer as " xmlns:n=&quot;urn:x&quot;" and read back as a text node.
/// </summary>
public sealed class CopyOfNamespaceNodeTests
{
    [Fact]
    public async Task A_copied_namespace_node_stays_a_namespace_node()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
            "<xsl:template name='xsl:initial-template'>" +
            "<xsl:variable name='ns' as='node()'><xsl:namespace name='n'>urn:x</xsl:namespace></xsl:variable>" +
            "<xsl:variable name='copy' as='node()'><xsl:copy-of select='$ns'/></xsl:variable>" +
            "<out><xsl:value-of select=\"$copy instance of namespace-node(), name($copy), string($copy)\"/></out>" +
            "</xsl:template></xsl:stylesheet>");
        (await t.TransformAsync((string?)null)).Should().Contain("<out>true n urn:x</out>");
    }
}
