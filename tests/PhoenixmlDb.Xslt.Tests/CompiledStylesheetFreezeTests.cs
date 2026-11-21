using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A compiled stylesheet is read-only once it is compiled: transforms, in parallel and over inputs that
/// declare their namespaces in different orders, must not change it. The test dumps the whole compiled
/// object graph (the xslt and XQuery syntax trees, by reflection) after compiling and again after the
/// transforms, and compares the two.
/// </summary>
public class CompiledStylesheetFreezeTests
{
    private const string Stylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                        xmlns:a="urn:a" xmlns:b="urn:b" xmlns:f="urn:f" exclude-result-prefixes="#all">
          <xsl:strip-space elements="a:*"/>
          <xsl:variable name="count" static="yes" select="2"/>
          <xsl:key name="k" match="b:y" use="@n"/>
          <xsl:function name="f:twice" as="xs:integer" xmlns:xs="http://www.w3.org/2001/XMLSchema">
            <xsl:param name="i" as="xs:integer"/><xsl:sequence select="$i * $count"/>
          </xsl:function>
          <xsl:template match="/"><out><xsl:apply-templates select="//a:x"/><xsl:value-of select="count(key('k', '2')), f:twice(3)"/></out></xsl:template>
          <xsl:template match="a:x[b:y]"><hit n="{count(b:y)}"><xsl:number count="b:y" level="any"/><xsl:apply-templates/></hit></xsl:template>
          <xsl:template match="b:y[@n = '2']" priority="2">two</xsl:template>
          <xsl:template match="text()"/>
        </xsl:stylesheet>
        """;

    private static readonly string[] Inputs =
    [
        """<r xmlns:a="urn:a" xmlns:b="urn:b"><a:x><b:y n="1"/><b:y n="2"/></a:x></r>""",
        """<r xmlns:b="urn:b" xmlns:a="urn:a"><b:q/><a:x> <b:y n="2"/> </a:x></r>""",
    ];

    [Fact]
    public async Task TransformsDoNotChangeTheCompiledStylesheet()
    {
        var first = new XsltTransformer();
        await first.LoadStylesheetAsync(Stylesheet);
        var compiled = StylesheetOf(first);
        var before = Dump(compiled);

        // Transforms on the compiled stylesheet: sequentially, then on several threads.
        foreach (var input in Inputs)
            await first.TransformAsync(input);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(n => Task.Run(async () =>
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(Stylesheet);
            SetStylesheet(t, compiled);
            for (var i = 0; i < 3; i++)
                await t.TransformAsync(Inputs[(i + n) % Inputs.Length]);
        })));

        var after = Dump(compiled);
        FirstDifference(before, after).Should().BeNull();
    }

    private static FieldInfo StylesheetField =>
        typeof(XsltTransformer).GetField("_stylesheet", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static object StylesheetOf(XsltTransformer t) => StylesheetField.GetValue(t)!;

    private static void SetStylesheet(XsltTransformer t, object compiled) => StylesheetField.SetValue(t, compiled);

    private static string? FirstDifference(List<string> a, List<string> b)
    {
        var before = new HashSet<string>(a, StringComparer.Ordinal);
        var changed = b.Where(l => !before.Contains(l)).ToList();
        if (changed.Count == 0 && a.Count == b.Count) return null;
        // Group by the field that changed: "Type.Field" at the end of the path.
        var groups = changed.Select(l => l[..l.IndexOf('=', StringComparison.Ordinal)])
            .Select(path => path[(path.LastIndexOf('/') + 1)..])
            .GroupBy(g => g, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} x{g.Count()}");
        return $"{changed.Count} changed lines ({a.Count} -> {b.Count}): " + string.Join("; ", groups)
            + "\n  e.g. " + string.Join("\n  e.g. ", changed.Take(3));
    }

    private static List<string> Dump(object root)
    {
        var lines = new List<string>();
        Walk(root, "root", lines, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        return lines;
    }

    private static bool IsTreeType(Type t) =>
        t.Namespace is { } ns && (ns.StartsWith("PhoenixmlDb.Xslt.Ast", StringComparison.Ordinal)
            || ns.StartsWith("PhoenixmlDb.XQuery.Ast", StringComparison.Ordinal));

    private static string Scalar(object? o) => o switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => o.ToString() ?? "",
    };

    private static void Walk(object? o, string path, List<string> lines, HashSet<object> seen, int depth)
    {
        if (o is null || o is string || o is Uri || o.GetType().IsValueType)
        {
            lines.Add(path + "=" + Scalar(o));
            return;
        }
        var t = o.GetType();
        if (depth > 400 || !seen.Add(o)) { lines.Add(path + "=^" + t.Name); return; }
        if (o is IDictionary dict)
        {
            var keys = new List<(string Key, object? Value)>();
            foreach (DictionaryEntry e in dict) keys.Add((Scalar(e.Key), e.Value));
            lines.Add(path + "=dict(" + keys.Count + ")");
            foreach (var (k, v) in keys.OrderBy(e => e.Key, StringComparer.Ordinal)) Walk(v, path + "[" + k + "]", lines, seen, depth + 1);
            return;
        }
        if (o is IEnumerable seq && !IsTreeType(t))
        {
            var i = 0;
            foreach (var item in seq) Walk(item, path + "[" + i++ + "]", lines, seen, depth + 1);
            lines.Add(path + "=list(" + i + ")");
            return;
        }
        if (!IsTreeType(t)) { lines.Add(path + "=" + t.Name); return; }
        for (var type = t; type is not null && type != typeof(object); type = type.BaseType)
            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                Walk(f.GetValue(o), path + "/" + t.Name + "." + f.Name.Replace("k__BackingField", "", StringComparison.Ordinal).Trim('<', '>'), lines, seen, depth + 1);
    }
}
