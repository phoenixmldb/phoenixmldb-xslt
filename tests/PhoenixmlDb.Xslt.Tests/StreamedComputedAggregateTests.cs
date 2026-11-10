using System.Text;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An aggregate over a value computed from each element (<c>sum(root/item/xs:decimal(.))</c>,
/// <c>max(a/b/xs:date(.))</c>, <c>sum(data(root/item))</c>) had no streamed form, so a streamable
/// stylesheet read the whole input into a tree first: 1 GiB per million items (xslt#462). The
/// streaming pass now folds it as it reads. Each case must give what the same stylesheet gives
/// on a tree, value, type and error alike.
/// </summary>
public sealed class StreamedComputedAggregateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-fold").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Numbers = "<root><item>5</item><item>2.5</item><item>-1</item></root>";
    private const string Integers = "<root><item>5</item><item>7</item><item>11</item></root>";
    private const string Empty = "<root/>";
    // The string value of an element with children is the text of all its descendants: "12".
    private const string Nested = "<root><item><a>1</a><b>2</b></item><item>3</item></root>";
    // Descendant selection with an item inside an item: the outer's string value is "12".
    private const string Recursive = "<root><item>1<item>2</item></item><item>4</item></root>";
    private const string Tokens = "<root><item>1 2 3</item><item>4</item></root>";
    private const string NotANumber = "<root><item>5</item><item>x</item></root>";
    private const string Dates = "<root><item>2020-01-01</item><item>2021-05-05</item><item>2019-12-31</item></root>";
    private const string Durations = "<root><item>PT1H</item><item>PT30M</item></root>";

    // The aggregate is the whole select: the document rule streams it.
    public static TheoryData<string, string> Folded => new()
    {
        { "sum(root/item/xs:decimal(.))", Numbers },
        { "sum(root/item!xs:decimal(.))", Numbers },
        { "sum(root/item/number())", Numbers },
        { "sum(root/item/data())", Numbers },
        { "sum(data(root/item))", Numbers },
        { "sum(root/item/xs:integer(.))", Integers },
        { "sum(root/item/(xs:decimal(.) * 2))", Numbers },
        { "sum(root/item/xs:decimal(.))", Empty },
        { "sum(root/item/xs:decimal(.), 'none')", Empty },
        { "sum(root/item/xs:decimal(.), 'none')", Numbers },
        { "count(root/item/xs:decimal(.))", Numbers },
        { "count(root/item!xs:NMTOKENS(.))", Tokens },
        { "sum(root/item!xs:NMTOKENS(.)!xs:decimal(.))", Tokens },
        { "max(root/item/xs:decimal(.))", Numbers },
        { "min(root/item/number())", Numbers },
        { "max(root/item/string())", Numbers },
        { "max(root/item/xs:decimal(.))", Empty },
        { "sum(root/item/xs:decimal(.))", Nested },
        { "sum(root//item/xs:decimal(.))", Recursive },
        { "sum(root/item/xs:decimal(.))", NotANumber },
        { "sum(root/item/number())", NotANumber },
        { "sum(root/item/string())", Numbers },
        { "max(root/item/xs:date(.))", Dates },
        { "min(root/item/xs:date(.))", Dates },
        { "sum(root/item/xs:dayTimeDuration(.))", Durations },
    };

    // Parity only. The aggregate inside another expression, and an absolute or // path in the
    // document rule, are not streamed there; they must still answer as on a tree, and in
    // xsl:source-document they take the fold.
    public static TheoryData<string, string> Other => new()
    {
        { "sum(root/item/xs:integer(.)) instance of xs:integer", Integers },
        { "sum(root/item/xs:decimal(.)) instance of xs:decimal", Numbers },
        { "sum(/root/item/xs:decimal(.))", Numbers },
        { "sum(//item/xs:decimal(.))", Recursive },
    };

    public static TheoryData<string, string> All
    {
        get
        {
            var all = new TheoryData<string, string>();
            foreach (var row in Folded.Concat(Other))
            {
                var (select, input) = row.Data;
                all.Add(select, input);
            }
            return all;
        }
    }

    [Theory]
    [MemberData(nameof(Folded))]
    public async Task The_document_rule_streams_and_answers_as_on_a_tree(string select, string input)
    {
        var stylesheet = DocumentRule(select);

        var tree = await RunAsync(stylesheet, input, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, input, disableStreaming: false);

        streamed.Outcome.Should().Be(tree.Outcome);
        streamed.Streamed.Should().BeTrue("the aggregate must be folded by the streaming pass, not read into a tree");
    }

    [Theory]
    [MemberData(nameof(Other))]
    public async Task Other_document_rules_answer_as_on_a_tree(string select, string input)
    {
        var stylesheet = DocumentRule(select);

        var tree = await RunAsync(stylesheet, input, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, input, disableStreaming: false);

        streamed.Outcome.Should().Be(tree.Outcome);
    }

    [Fact]
    public async Task A_text_value_template_streams_as_in_the_report()
    {
        // The shape of xslt#462: the aggregate in a text value template.
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" expand-text="yes">
              <xsl:mode streamable="yes"/>
              <xsl:template match="/"><sum>{sum(root/item/xs:decimal(.))}</sum></xsl:template>
            </xsl:stylesheet>
            """;

        var tree = await RunAsync(stylesheet, Numbers, disableStreaming: true);
        var streamed = await RunAsync(stylesheet, Numbers, disableStreaming: false);

        streamed.Outcome.Should().Be(tree.Outcome).And.Contain("<sum>6.5</sum>");
        streamed.Streamed.Should().BeTrue();
    }

    private static string DocumentRule(string select) => $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
          <xsl:mode streamable="yes"/>
          <xsl:template match="/"><out><xsl:value-of select="{select.Replace("'", "&apos;")}"/></out></xsl:template>
        </xsl:stylesheet>
        """;

    [Theory]
    [MemberData(nameof(All))]
    public async Task Source_document_answers_as_on_a_tree(string select, string input)
    {
        var inputPath = Path.Combine(_dir, "in.xml");
        await File.WriteAllTextAsync(inputPath, input);
        string Stylesheet(string streamable) => $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:template name="main"><xsl:source-document streamable="{streamable}" href="in.xml"><out><xsl:value-of select="{select.Replace("'", "&apos;")}"/></out></xsl:source-document></xsl:template>
            </xsl:stylesheet>
            """;

        var tree = await RunNamedAsync(Stylesheet("no"));
        var streamed = await RunNamedAsync(Stylesheet("yes"));

        streamed.Should().Be(tree);
    }

    private static async Task<(string Outcome, bool Streamed)> RunAsync(string stylesheet, string input, bool disableStreaming)
    {
        var transformer = new XsltTransformer { DisableStreaming = disableStreaming };
        await transformer.LoadStylesheetAsync(stylesheet);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var output = new StringWriter();
        try
        {
            await transformer.TransformAsync(source, output);
            return (output.ToString(), transformer.LastTransformStreamed);
        }
        catch (Exception e) when (ErrorCode(e) is { } code)
        {
            return ("error " + code, transformer.LastTransformStreamed);
        }
    }

    private async Task<string> RunNamedAsync(string stylesheet)
    {
        var path = Path.Combine(_dir, "s.xsl");
        await File.WriteAllTextAsync(path, stylesheet);
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet, new Uri(path));
        transformer.SetInitialTemplate("main");
        try
        {
            return await transformer.TransformAsync("<dummy/>");
        }
        catch (Exception e) when (ErrorCode(e) is { } code)
        {
            return "error " + code;
        }
    }

    private static string? ErrorCode(Exception e) => e switch
    {
        PhoenixmlDb.Xslt.Engine.XsltException x => x.ErrorCode ?? (x.InnerException is { } xi ? ErrorCode(xi) : null),
        PhoenixmlDb.XQuery.Execution.XQueryRuntimeException q => q.ErrorCode,
        PhoenixmlDb.XQuery.Functions.XQueryException f => f.ErrorCode,
        _ => e.InnerException is { } inner ? ErrorCode(inner) : null,
    };
}
