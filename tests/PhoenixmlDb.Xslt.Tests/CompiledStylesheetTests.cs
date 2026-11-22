using AwesomeAssertions;
using PhoenixmlDb.XQuery.Security;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The compile-once API: <see cref="CompiledStylesheet"/> compiles a stylesheet one time, and each
/// <see cref="CompiledStylesheet.CreateTransformer"/> gives a transformer of its own (parameters,
/// listeners, run-time settings) that shares the compiled stylesheet.
/// </summary>
public class CompiledStylesheetTests
{
    private const string Greeting = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:param name="who" select="'nobody'"/>
          <xsl:output method="text"/>
          <xsl:template match="/">hello <xsl:value-of select="$who"/> (<xsl:value-of select="count(//item)"/>)</xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task ATransformerFromTheCompiledStylesheetGivesTheSameResultAsLoadStylesheet()
    {
        var compiled = await CompiledStylesheet.CompileAsync(Greeting);
        var loaded = new XsltTransformer();
        await loaded.LoadStylesheetAsync(Greeting);

        var fromCompiled = await compiled.CreateTransformer().TransformAsync("<r><item/><item/></r>");

        fromCompiled.Should().Be(await loaded.TransformAsync("<r><item/><item/></r>"));
        fromCompiled.Should().Be("hello nobody (2)");
    }

    [Fact]
    public async Task EachTransformerHasItsOwnParameters()
    {
        var compiled = await CompiledStylesheet.CompileAsync(Greeting);
        var a = compiled.CreateTransformer();
        var b = compiled.CreateTransformer();
        a.SetParameter("who", "alice");

        (await a.TransformAsync("<r/>")).Should().Be("hello alice (0)");
        (await b.TransformAsync("<r/>")).Should().Be("hello nobody (0)");
        (await compiled.CreateTransformer().TransformAsync("<r/>")).Should().Be("hello nobody (0)");
    }

    [Fact]
    public async Task TransformersOnManyThreadsGetTheirOwnResults()
    {
        var compiled = await CompiledStylesheet.CompileAsync(Greeting);

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(n => Task.Run(async () =>
        {
            var t = compiled.CreateTransformer();
            t.SetParameter("who", "t" + n);
            var input = "<r>" + string.Concat(Enumerable.Repeat("<item/>", n)) + "</r>";
            return (n, Output: await t.TransformAsync(input));
        })));

        foreach (var (n, output) in results)
            output.Should().Be($"hello t{n} ({n})");
    }

    [Fact]
    public async Task StaticParametersApplyAtCompileTimeAndAsGlobalParameters()
    {
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:param name="debug" static="yes" select="false()"/>
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:value-of select="$debug"/>:<xsl:value-of use-when="$debug" select="'on'"/></xsl:template>
            </xsl:stylesheet>
            """;
        var options = new XsltCompileOptions { StaticParameters = new Dictionary<string, string> { ["debug"] = "true()" } };

        var compiled = await CompiledStylesheet.CompileAsync(xslt, options: options);

        (await compiled.CreateTransformer().TransformAsync("<r/>")).Should().Be("true:on");
        (await (await CompiledStylesheet.CompileAsync(xslt)).CreateTransformer().TransformAsync("<r/>")).Should().Be("false:");
    }

    [Fact]
    public async Task ACreatedTransformerStartsWithTheCompileSettings()
    {
        var policy = ResourcePolicy.CreateBuilder().Build();
        var provider = new PhoenixmlDb.XQuery.XsdSchemaProvider();
        var modules = new Dictionary<string, IReadOnlyList<string>> { ["urn:m"] = ["m.xq"] };
        var preloaded = new PreloadedResources();
        var options = new XsltCompileOptions
        {
            ResourcePolicy = policy,
            RegexMatchTimeout = TimeSpan.FromSeconds(3),
            SchemaProvider = provider,
            AllowDtdProcessing = true,
            SchemaAware = false,
            XQueryModules = modules,
            PreloadedResources = preloaded,
        };
        var baseUri = new Uri("file:///styles/greeting.xsl");

        var compiled = await CompiledStylesheet.CompileAsync(Greeting, baseUri, options);
        var t = compiled.CreateTransformer();

        compiled.BaseUri.Should().Be(baseUri);
        t.ResourcePolicy.Should().BeSameAs(policy);
        t.RegexMatchTimeout.Should().Be(TimeSpan.FromSeconds(3));
        t.SchemaProvider.Should().BeSameAs(provider);
        t.AllowDtdProcessing.Should().BeTrue();
        t.SchemaAware.Should().BeFalse();
        t.XQueryModules.Should().BeSameAs(modules);
        t.PreloadedResources.Should().BeSameAs(preloaded);
        compiled.CreateTransformer().SchemaProvider.Should().BeSameAs(provider, "every transformer shares the compiled stylesheet's schema provider");
    }

    [Fact]
    public async Task WithoutASchemaProviderInTheOptionsEachCompiledStylesheetGetsItsOwn()
    {
        var one = await CompiledStylesheet.CompileAsync(Greeting);
        var two = await CompiledStylesheet.CompileAsync(Greeting);

        one.CreateTransformer().SchemaProvider.Should().NotBeNull();
        one.CreateTransformer().SchemaProvider.Should().BeSameAs(one.CreateTransformer().SchemaProvider);
        one.CreateTransformer().SchemaProvider.Should().NotBeSameAs(two.CreateTransformer().SchemaProvider);
    }

    private const string JsonToXml = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                        xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xsl:output method="text"/>
          <xsl:template match="/">
            <xsl:variable name="j" select="json-to-xml(string(.), map { 'validate': true() })"/>
            <xsl:value-of select="data(($j//*:number)[1]) instance of xs:double, count($j//*:number)"/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private const string FnNamespace = "http://www.w3.org/2005/xpath-functions";

    [Fact]
    public async Task ACompiledStylesheetsSchemaProviderHasTheFnSchemasBeforeAnyTransformation()
    {
        // fn:json-to-xml with validate types its result with the fn namespace's schema, which the
        // provider adds on first use: during a transformation, while the stylesheet's other
        // transformations read the provider. A shared stylesheet's provider gets it when it is compiled.
        var compiled = await CompiledStylesheet.CompileAsync(JsonToXml);
        var loaded = new XsltTransformer();
        await loaded.LoadStylesheetAsync(JsonToXml);

        compiled.CreateTransformer().SchemaProvider!.HasSchemaType(FnNamespace, "match-type").Should().BeTrue();
        loaded.SchemaProvider!.HasSchemaType(FnNamespace, "match-type").Should().BeFalse("LoadStylesheetAsync is unchanged: one transformer, one transformation at a time");
        (await loaded.TransformAsync("<r>[1, 2]</r>")).Should().Be("true 2");
        loaded.SchemaProvider!.HasSchemaType(FnNamespace, "match-type").Should().BeTrue();
    }

    [Fact]
    public async Task ValidatedJsonToXmlOnManyThreadsAtOnceGivesTypedResults()
    {
        for (var round = 0; round < 4; round++)
        {
            var compiled = await CompiledStylesheet.CompileAsync(JsonToXml);
            using var start = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, 16).Select(n => Task.Run(async () =>
            {
                var t = compiled.CreateTransformer();
                start.Wait();
                return await t.TransformAsync("<r>[" + string.Join(", ", Enumerable.Range(0, n + 1)) + "]</r>");
            })).ToList();
            start.Set();
            var outputs = await Task.WhenAll(tasks);
            for (var n = 0; n < outputs.Length; n++)
                outputs[n].Should().Be($"true {n + 1}");
        }
    }

    [Fact]
    public void CompileReturnsOnTheCallingThread()
    {
        var thread = Environment.CurrentManagedThreadId;

        var compiled = CompiledStylesheet.Compile(Greeting);

        Environment.CurrentManagedThreadId.Should().Be(thread);
        compiled.CreateTransformer().Transform("<r/>").Should().Be("hello nobody (0)");
    }

    [Fact]
    public async Task ACancelledCompileThrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => CompiledStylesheet.CompileAsync(Greeting, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AStaticErrorIsAnXsltException()
    {
        const string broken = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><xsl:no-such-instruction/></xsl:template>
            </xsl:stylesheet>
            """;

        var act = () => CompiledStylesheet.CompileAsync(broken);

        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE0010");
    }

    [Fact]
    public async Task AnXPathSyntaxErrorIsAnXQueryParseExceptionAsWithLoadStylesheet()
    {
        const string broken = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><xsl:value-of select="1 +"/></xsl:template>
            </xsl:stylesheet>
            """;

        var compile = () => CompiledStylesheet.CompileAsync(broken);
        var load = () => new XsltTransformer().LoadStylesheetAsync(broken);

        await compile.Should().ThrowAsync<PhoenixmlDb.XQuery.Parser.XQueryParseException>();
        await load.Should().ThrowAsync<PhoenixmlDb.XQuery.Parser.XQueryParseException>();
    }

    [Fact]
    public async Task LoadStylesheetOnACreatedTransformerReplacesOnlyItsOwnStylesheet()
    {
        var compiled = await CompiledStylesheet.CompileAsync(Greeting);
        var replaced = compiled.CreateTransformer();

        await replaced.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">other</xsl:template>
            </xsl:stylesheet>
            """);

        (await replaced.TransformAsync("<r/>")).Should().Be("other");
        (await compiled.CreateTransformer().TransformAsync("<r/>")).Should().Be("hello nobody (0)");
    }
}
