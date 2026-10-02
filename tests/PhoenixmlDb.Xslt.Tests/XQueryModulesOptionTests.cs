using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XsltTransformer.XQueryModules supplies the module for a load-xquery-module call that names a
/// namespace without location hints, at run time and in static expressions. A static variable
/// holding a function item is an instance of function(*) in use-when.
/// </summary>
public sealed class XQueryModulesOptionTests : IDisposable
{
    private const string ModuleUri = "urn:test:module";
    private readonly string _file = Path.Combine(Path.GetTempPath(), "phx-mod-" + Guid.NewGuid().ToString("N") + ".xqm");

    public XQueryModulesOptionTests() => File.WriteAllText(_file,
        $"module namespace m = \"{ModuleUri}\"; declare variable $m:v := 'var1'; declare function m:f() {{ 'func1' }};");

    public void Dispose()
    {
        try { File.Delete(_file); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(string top, string body)
    {
        var t = new XsltTransformer { XQueryModules = new Dictionary<string, IReadOnlyList<string>> { [ModuleUri] = [_file] } };
        await t.LoadStylesheetAsync(
            "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" + top +
            "<xsl:template name='xsl:initial-template'><out>" + body + "</out></xsl:template></xsl:stylesheet>");
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task A_module_without_location_hints_loads_at_run_time()
        => (await RunAsync("", $"<xsl:value-of select=\"load-xquery-module('{ModuleUri}')?variables(QName('{ModuleUri}', 'v'))\"/>"))
            .Should().Contain("<out>var1</out>");

    [Fact]
    public async Task A_static_variable_loads_a_module_and_use_when_sees_the_function()
        => (await RunAsync(
                $"<xsl:variable name='m' select=\"load-xquery-module('{ModuleUri}')\" static='yes'/>" +
                $"<xsl:variable name='f' select=\"$m?functions(QName('{ModuleUri}', 'f'))?0\" static='yes'/>",
                "<r xsl:use-when=\"$f instance of function(*)\"><xsl:sequence select=\"$f()\"/></r>"))
            .Should().Contain("<r>func1</r>");
}
