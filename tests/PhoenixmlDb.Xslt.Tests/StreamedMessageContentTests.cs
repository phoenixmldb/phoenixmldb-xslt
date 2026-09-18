using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An <c>xsl:message</c> inside a streamed <c>xsl:source-document</c> must receive the items its
/// content produces, including those from a streamable <c>xsl:for-each</c> over the stream.
///
/// <c>StreamingExpressionScanner</c> registers a <c>ForEachSubscription</c> for every streamable
/// <c>for-each</c> it can reach, and the dispatcher drives those during the streaming pass. It had
/// no <c>xsl:message</c> arm at all, so a <c>for-each</c> inside one was never registered: nothing
/// drove it, its select ran against the synthetic empty document, and the streamed items vanished
/// from the message while non-streamed content in the same message survived (#148).
///
/// Same shape as #117, where the scanner did not descend into <c>xsl:map</c>/<c>xsl:map-entry</c>
/// and a streamed map came out empty. Descending is safe here for the same reason it is safe for
/// <c>xsl:where-populated</c> and not for <c>xsl:on-empty</c>: a message's content is ALWAYS
/// evaluated, so a subscription inside it cannot dispatch for content that is then discarded.
///
/// Messages are captured through <c>MessageListener</c>, because the transformer discards them
/// when no listener is set.
/// </summary>
public sealed class StreamedMessageContentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-smsg-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const string Source = """
        <account>
          <transaction value="-15.00"/>
          <transaction value="-5.00"/>
          <transaction value="-2.33"/>
        </account>
        """;

    private async Task<List<string>> RunAsync(string stylesheet, string? initialTemplate)
    {
        var srcPath = Path.Combine(_dir, "in.xml");
        await File.WriteAllTextAsync(srcPath, Source);
        var messages = new List<string>();
        var t = new XsltTransformer { MessageListener = (m, _) => messages.Add(m) };
        await t.LoadStylesheetAsync(stylesheet.Replace("SRC", new Uri(srcPath).AbsoluteUri, StringComparison.Ordinal),
            new Uri(Path.Combine(_dir, "s.xsl")));
        if (initialTemplate != null)
            t.SetInitialTemplate(initialTemplate);
        await t.TransformAsync(Source);
        return messages;
    }

    /// <summary>
    /// The reported shape: one xsl:message whose content mixes streamed items with constants.
    /// Every streamed value must appear, and it must be ONE message — the constants arriving alone
    /// was the symptom.
    /// </summary>
    [Fact]
    public async Task StreamedForEach_InsideMessage_DeliversItsItems()
    {
        var messages = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="main">
                <out>
                  <xsl:source-document streamable="yes" href="SRC">
                    <xsl:message>
                      <xsl:for-each select="data(account/transaction/@value), 101, 102">
                        <xsl:sequence select="data(.)"/>
                      </xsl:for-each>
                    </xsl:message>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """, "main");

        var joined = string.Join(" | ", messages);
        joined.Should().Contain("-15.00", $"streamed items must reach the message; got: {joined}");
        joined.Should().Contain("-5.00", $"got: {joined}");
        joined.Should().Contain("-2.33", $"got: {joined}");
        joined.Should().Contain("101", $"got: {joined}");
    }

    /// <summary>
    /// Guard against fixing the streamed path by breaking the ordinary one: the same body,
    /// unstreamed, already worked and must keep working.
    /// </summary>
    [Fact]
    public async Task UnstreamedForEach_InsideMessage_StillDeliversItsItems()
    {
        var messages = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <out>
                  <xsl:message>
                    <xsl:for-each select="data(account/transaction/@value), 101, 102">
                      <xsl:sequence select="data(.)"/>
                    </xsl:for-each>
                  </xsl:message>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """, null);

        messages.Should().ContainSingle();
        messages[0].Should().Contain("-15.00").And.Contain("-2.33").And.Contain("102");
    }

    /// <summary>
    /// Guard: a streamed message with no for-each at all still emits exactly its content, so the
    /// new scanner arm cannot be credited for messages it never had to reach.
    /// </summary>
    [Fact]
    public async Task StreamedMessage_WithoutForEach_IsUnaffected()
    {
        var messages = await RunAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="main">
                <out>
                  <xsl:source-document streamable="yes" href="SRC">
                    <xsl:message>plain</xsl:message>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """, "main");

        messages.Should().ContainSingle();
        messages[0].Trim().Should().Be("plain");
    }
}
