using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A declared default collection with no documents is the empty sequence, not FODC0002; with no
/// default collection declared, collection() is still FODC0002.
/// </summary>
public sealed class DefaultCollectionTests
{
    private static async Task<string> RunAsync(bool declareEmptyDefault)
    {
        var t = new XsltTransformer();
        if (declareEmptyDefault)
            t.SetCollection("", []);
        await t.LoadStylesheetAsync("<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
            "<xsl:template name='xsl:initial-template'><out><xsl:value-of select='count(collection())'/></out></xsl:template></xsl:stylesheet>");
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task An_empty_declared_default_collection_is_the_empty_sequence()
        => (await RunAsync(declareEmptyDefault: true)).Should().Contain("<out>0</out>");

    [Fact]
    public async Task No_default_collection_is_still_FODC0002()
        => await FluentActions.Awaiting(() => RunAsync(declareEmptyDefault: false))
            .Should().ThrowAsync<PhoenixmlDb.XQuery.Execution.XQueryRuntimeException>().Where(e => e.ErrorCode == "FODC0002");
}
