using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// In a streamed xsl:source-document, accumulator-after() evaluated before a consuming instruction
/// consumes the input itself, so copy-of select="." afterwards reads it twice: XTSE3430 at compile
/// time. The same call AFTER the consuming instruction stays streamable.
/// </summary>
public sealed class AccumulatorAfterSourceDocumentTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "phx-acc-" + Guid.NewGuid().ToString("N") + ".xml");

    public AccumulatorAfterSourceDocumentTests() => File.WriteAllText(_file, "<r><x/></r>");

    public void Dispose()
    {
        try { File.Delete(_file); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // The check runs when xsl:source-document is evaluated, as in the W3C case.
    private async Task RunAsync(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(
            "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" +
            "<xsl:accumulator name='a' initial-value='0' streamable='yes'><xsl:accumulator-rule match='*' select='$value + 1'/></xsl:accumulator>" +
            $"<xsl:template name='main'><out><xsl:source-document streamable='yes' use-accumulators='a' href='{new Uri(_file).AbsoluteUri}'>" + body +
            "</xsl:source-document></out></xsl:template></xsl:stylesheet>");
        t.SetInitialTemplate("main");
        await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task Accumulator_after_before_a_copy_of_the_context_is_XTSE3430()
        => (await FluentActions.Awaiting(() => RunAsync("<xsl:value-of select=\"accumulator-after('a')\"/><xsl:copy-of select='.'/>"))
                .Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTSE3430");

    [Fact]
    public async Task Accumulator_after_following_the_copy_is_accepted()
        => await FluentActions.Awaiting(() => RunAsync("<xsl:copy-of select='.'/><xsl:value-of select=\"accumulator-after('a')\"/>"))
            .Should().NotThrowAsync();
}
