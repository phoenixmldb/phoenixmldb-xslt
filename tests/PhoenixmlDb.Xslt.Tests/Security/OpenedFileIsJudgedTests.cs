using AwesomeAssertions;
using PhoenixmlDb.XQuery.Security;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests.Security;

/// <summary>
/// Under a resource policy a file a stylesheet reads is judged by the file actually opened. The
/// policy resolved every link on the path and judged where it led, and the reader then opened the
/// path again by name: a link put in place of the file between the two was followed out of the
/// allowed roots, and the transformation read a file the policy refuses.
/// </summary>
public sealed class OpenedFileIsJudgedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-xsl-opened").FullName;
    private readonly string _allowed;
    private readonly string _secret;

    public OpenedFileIsJudgedTests()
    {
        _allowed = Directory.CreateDirectory(Path.Combine(_dir, "allowed")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
        _secret = Path.Combine(outside, "secret.xml");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("<xsl:value-of select=\"unparsed-text('{0}')\"/>")]
    [InlineData("<xsl:value-of select=\"unparsed-text('{0}', 'utf-8')\"/>")]
    [InlineData("<xsl:value-of select=\"string(document('{0}'))\"/>")]
    [InlineData("<xsl:source-document href=\"{0}\"><xsl:value-of select=\"string(.)\"/></xsl:source-document>")]
    public async Task A_link_swapped_in_and_out_during_reads_is_never_followed(string body)
    {
        // A file inside the allowed root that another thread keeps replacing with a link to a
        // file outside it, and back. A read gives the file inside or a refusal, never the secret.
        await File.WriteAllTextAsync(_secret, "<r>SECRET</r>", TestContext.Current.CancellationToken);
        var file = Path.Combine(_allowed, "a.xml");
        var regular = Path.Combine(_allowed, ".regular");
        var link = Path.Combine(_allowed, ".link");
        await File.WriteAllTextAsync(file, "<r>inside</r>", TestContext.Current.CancellationToken);

        using var stop = new CancellationTokenSource();
        var swapper = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    File.WriteAllText(regular, "<r>inside</r>");
                    File.Move(regular, file, overwrite: true);
                    if (File.Exists(link) || new FileInfo(link).LinkTarget != null) File.Delete(link);
                    File.CreateSymbolicLink(link, _secret);
                    File.Move(link, file, overwrite: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }, TestContext.Current.CancellationToken);

        var stylesheet = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:try>{{string.Format(System.Globalization.CultureInfo.InvariantCulture, body, new Uri(file).AbsoluteUri)}}<xsl:catch>refused</xsl:catch></xsl:try></xsl:template>
            </xsl:stylesheet>
            """;
        var leaked = 0;
        var reads = 0;
        try
        {
            for (var i = 0; i < 300; i++)
            {
                var transformer = new XsltTransformer
                {
                    ResourcePolicy = ResourcePolicy.CreateBuilder()
                        .AllowReadFrom("file", null, _allowed + Path.DirectorySeparatorChar)
                        .Build(),
                };
                await transformer.LoadStylesheetAsync(stylesheet);
                string result;
                try { result = await transformer.TransformAsync("<x/>"); }
                catch (Exception e) when (e is XsltException or ResourceAccessDeniedException) { result = "refused"; }
                reads++;
                if (result.Contains("SECRET", StringComparison.Ordinal))
                    leaked++;
            }
        }
        finally
        {
            await stop.CancelAsync();
            await swapper;
        }

        leaked.Should().Be(0, $"{reads} reads of a file swapped with a link to one outside the allowed root");
    }
}
