using System.Xml.Linq;
using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Equal sets of namespace bindings in one stylesheet module share one dictionary (#401): each
/// instruction kept its own copy. Sets that differ in order are kept apart, since a literal
/// result element writes its declarations in that order.
/// </summary>
public sealed class SharedNamespaceSetsTests
{
    [Fact]
    public void Equal_bindings_in_one_module_are_one_dictionary()
    {
        var doc = XDocument.Parse("<r xmlns:a='urn:a'><x/><y/></r>");
        var x = doc.Root!.Elements().First();
        var y = doc.Root!.Elements().Last();

        var first = NamespaceSets.Shared(x, new Dictionary<string, string> { ["a"] = "urn:a", ["b"] = "urn:b" });
        var second = NamespaceSets.Shared(y, new Dictionary<string, string> { ["a"] = "urn:a", ["b"] = "urn:b" });
        var reordered = NamespaceSets.Shared(y, new Dictionary<string, string> { ["b"] = "urn:b", ["a"] = "urn:a" });
        var other = NamespaceSets.Shared(XDocument.Parse("<r/>").Root!, new Dictionary<string, string> { ["a"] = "urn:a", ["b"] = "urn:b" });

        second.Should().BeSameAs(first);
        reordered.Should().NotBeSameAs(first);
        reordered.Keys.Should().Equal("b", "a");
        other.Should().NotBeSameAs(first);
    }

    [Fact]
    public void A_shared_set_cannot_be_changed_and_is_not_the_callers_dictionary()
    {
        var bindings = new Dictionary<string, string> { ["a"] = "urn:a" };
        var shared = NamespaceSets.Shared(XDocument.Parse("<r/>").Root!, bindings);
        bindings["b"] = "urn:b";

        shared.Keys.Should().Equal("a");
        var asDictionary = (IDictionary<string, string>)shared;
        asDictionary.IsReadOnly.Should().BeTrue();
        var write = () => asDictionary["c"] = "urn:c";
        write.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task Literal_result_elements_keep_their_namespace_declarations()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:a="urn:a" version="3.0">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template match="/"><r><p xmlns:b="urn:b"/><q xmlns:b="urn:b"/><s xmlns:c="urn:c"/></r></xsl:template>
            </xsl:stylesheet>
            """);
        (await transformer.TransformAsync("<x/>"))
            .Should().Be("<r xmlns:a=\"urn:a\"><p xmlns:b=\"urn:b\"/><q xmlns:b=\"urn:b\"/><s xmlns:c=\"urn:c\"/></r>");
    }
}
