using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.XQuery.Parser;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// BUGS.md #166: in a template rule whose pattern matches only document nodes and that has no name,
/// <c>root()</c> and <c>root(.)</c> evaluated with the rule's focus are the context item (XSLT 3.0
/// §19.8.9.18), and the parser writes them as it (<see cref="RootOfDocumentRewrite"/>), so that the
/// rule streams. Where the focus is another item (a grouping key, the body of a loop), in a rule with
/// a name (called with another context item), and in a rule whose pattern also matches elements, the
/// calls are kept.
/// </summary>
public sealed class RootOfDocumentRewriteTests
{
    private const string Books = """<books><book price="93" x="1"><title>A</title></book><book price="3000" x="2"><title>B</title></book></books>""";

    private static XsltTemplate Template(string template, string mode = "")
    {
        var parser = new StylesheetParser(new XQueryExpressionParser());
        var stylesheet = parser.Parse($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              {{mode}}
              {{template}}
            </xsl:stylesheet>
            """);
        return stylesheet.Templates[0];
    }

    private static T First<T>(XsltSequenceConstructor body) where T : XsltInstruction
        => body.Instructions.OfType<T>().First();

    [Fact]
    public void Root_with_the_rule_focus_is_the_context_item()
    {
        var t = Template("""<xsl:template match="/"><out n="{count(root()/books)}"><xsl:value-of select="count(root(.)//title)"/></out></xsl:template>""");

        var lre = First<XsltLiteralResultElement>(t.Body);
        lre.Attributes.Values.Single().Parts.OfType<AvtExpression>().Single().Expression.ToString().Should().NotContain("root(");
        First<XsltValueOf>(lre.Content).Select!.ToString().Should().NotContain("root(");
    }

    // Each instruction the rewrite visits, with root() in the operand that keeps the rule's focus:
    // no expression of the rule keeps a call.
    public static TheoryData<string> EveryKind => new()
    {
        """<xsl:sequence select="count(root()/books)"/>""",
        """<xsl:sequence><xsl:value-of select="count(root()/books)"/></xsl:sequence>""",
        """<xsl:copy-of select="root()/books"/>""",
        """<out xsl:expand-text="yes">{count(root()/books)}</out>""",
        """<xsl:value-of select="'x'" separator="{name(root()/*)}"/>""",
        """<xsl:value-of><xsl:value-of select="count(root()/books)"/></xsl:value-of>""",
        """<xsl:if test="root()/books"><y/></xsl:if>""",
        """<xsl:if test="true()"><xsl:value-of select="count(root()/books)"/></xsl:if>""",
        """<xsl:choose><xsl:when test="root()/books"><y/></xsl:when></xsl:choose>""",
        """<xsl:choose><xsl:when test="true()"><xsl:value-of select="count(root()/books)"/></xsl:when><xsl:otherwise><z/></xsl:otherwise></xsl:choose>""",
        """<xsl:choose><xsl:when test="false()"><y/></xsl:when><xsl:otherwise><xsl:value-of select="count(root(.)/books)"/></xsl:otherwise></xsl:choose>""",
        """<xsl:variable name="v" select="root()/books"/><xsl:sequence select="count($v)"/>""",
        """<xsl:variable name="v"><xsl:value-of select="count(root()/books)"/></xsl:variable><xsl:sequence select="$v"/>""",
        """<xsl:apply-templates select="root()/books"/>""",
        """<xsl:for-each select="root()/books"><x/></xsl:for-each>""",
        """<xsl:iterate select="root()/books/book"><x/></xsl:iterate>""",
        """<xsl:for-each-group select="root()/books/book" group-by="@x"><g/></xsl:for-each-group>""",
        """<out a="{count(root()/books)}" b="x"/>""",
    };

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void Every_place_with_the_rule_focus_is_rewritten(string body)
    {
        var rule = Template($"""<xsl:template match="/">{body}</xsl:template>""");

        Expressions(rule.Body).Should().NotBeEmpty().And.NotContain(e => e.Contains("root(", StringComparison.Ordinal));
    }

    // Every XQuery expression held by the instructions of a body, as text (walked with reflection,
    // in the test only).
    private static List<string> Expressions(object root)
    {
        var found = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object? o)
        {
            if (o is null || o is string || o.GetType().IsValueType || !seen.Add(o))
                return;
            if (o is XQueryExpression e)
            {
                found.Add(e.ToString() ?? "");
                return;
            }
            if (o is System.Collections.IDictionary d)
            {
                foreach (var v in d.Values)
                    Walk(v);
                return;
            }
            if (o is System.Collections.IEnumerable items)
            {
                foreach (var v in items)
                    Walk(v);
                return;
            }
            if (o.GetType().Namespace is not "PhoenixmlDb.Xslt.Ast")
                return;
            foreach (var property in o.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length == 0)
                    Walk(property.GetValue(o));
            }
        }
        Walk(root);
        return found;
    }

    [Fact]
    public void A_grouping_key_and_the_body_of_a_loop_keep_their_calls()
    {
        var t = Template("""
            <xsl:template match="/">
              <xsl:for-each-group select="root()/books/book" group-by="count(root()/books/book)"><g/></xsl:for-each-group>
              <xsl:for-each select="root()/books/book"><v><xsl:value-of select="count(root()//title)"/></v></xsl:for-each>
            </xsl:template>
            """);

        var group = First<XsltForEachGroup>(t.Body);
        group.Select.ToString().Should().NotContain("root(");
        group.GroupBy!.ToString().Should().Contain("root(");
        var loop = First<XsltForEach>(t.Body);
        loop.Select.ToString().Should().NotContain("root(");
        First<XsltValueOf>(First<XsltLiteralResultElement>(loop.Body).Content).Select!.ToString().Should().Contain("root(");
    }

    [Theory]
    [InlineData("""<xsl:template match="/" name="t"><n><xsl:value-of select="count(root()/books/book)"/></n></xsl:template>""")]
    [InlineData("""<xsl:template match="/ | book"><n><xsl:value-of select="count(root()/books/book)"/></n></xsl:template>""")]
    [InlineData("""<xsl:template match="book"><n><xsl:value-of select="count(root()/books/book)"/></n></xsl:template>""")]
    public void A_named_rule_and_a_rule_that_also_matches_elements_keep_their_calls(string template)
        => First<XsltValueOf>(First<XsltLiteralResultElement>(Template(template).Body).Content).Select!.ToString().Should().Contain("root(");

    [Fact]
    public void A_simplified_stylesheet_is_a_rule_for_the_document_node()
    {
        var parser = new StylesheetParser(new XQueryExpressionParser());
        var stylesheet = parser.Parse("""
            <out xsl:version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"><xsl:value-of select="count(root()/books)"/></out>
            """);

        Expressions(stylesheet.Templates[0].Body).Should().NotBeEmpty().And.NotContain(e => e.Contains("root(", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rule_with_nothing_to_rewrite_keeps_its_body()
    {
        var parser = new StylesheetParser(new XQueryExpressionParser());
        var stylesheet = parser.Parse("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><out><xsl:value-of select="count(books)"/></out></xsl:template>
            </xsl:stylesheet>
            """);
        var body = stylesheet.Templates[0].Body;

        RootOfDocumentRewrite.Apply(body).Should().BeSameAs(body);
    }

    // The value is that of root(): a named rule called with an element, and a rule that also
    // matches elements, give the document node's children (2 books), not the element's (0).
    [Fact]
    public async Task A_named_rule_for_the_document_node_called_with_an_element_reads_from_the_root()
    {
        var output = await RunAsync(
            """
            <xsl:template match="/"><r><xsl:apply-templates select="books/book[1]"/></r></xsl:template>
            <xsl:template match="/" name="t" mode="n"><n><xsl:value-of select="count(root()/books/book)"/></n></xsl:template>
            <xsl:template match="book"><xsl:call-template name="t"/></xsl:template>
            """, streamable: false);

        output.Should().Contain("<n>2</n>");
    }

    [Fact]
    public async Task A_rule_that_also_matches_elements_reads_from_the_root()
    {
        var output = await RunAsync(
            """
            <xsl:template match="/ | book"><n><xsl:value-of select="count(root()/books/book)"/></n><xsl:apply-templates select="books/book[1]"/></xsl:template>
            """, streamable: false);

        output.Should().Be("<n>2</n><n>2</n>");
    }

    // In a streamable mode such a key is XTSE3430 (a grouping key must be motionless, §19.8); the
    // rewrite is made in every mode.
    [Fact]
    public async Task A_grouping_key_with_root_has_the_item_as_focus()
    {
        const string Rules = """<xsl:template match="/"><xsl:for-each-group select="root()/books/book" group-by="count(root()/books/book)"><g k="{current-grouping-key()}" c="{count(current-group())}"/></xsl:for-each-group></xsl:template>""";

        var output = await RunAsync(Rules, streamable: false);

        output.Should().Be("""<g k="2" c="2"/>""");
    }

    private static async Task<string> RunAsync(string rules, bool streamable)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="{{(streamable ? "yes" : "no")}}" on-no-match="shallow-skip"/>
              <xsl:output omit-xml-declaration="yes"/>
              {{rules}}
            </xsl:stylesheet>
            """);
        if (!streamable)
            return await transformer.TransformAsync(Books);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Books));
        using var output = new StringWriter();
        await transformer.TransformAsync(source, output);
        return output.ToString();
    }

    // Each copy keeps every field of the original but the operands it is given: a property added
    // to one of these classes later and not copied fails here.
    public static TheoryData<Type> Copied => new()
    {
        typeof(XsltSequenceConstructor), typeof(XsltValueOf), typeof(XsltSequence), typeof(XsltCopyOf),
        typeof(XsltTextValueTemplate), typeof(XsltLiteralResultElement), typeof(XsltIf), typeof(XsltChoose),
        typeof(XsltWhen), typeof(XsltVariableInstruction), typeof(XsltApplyTemplates), typeof(XsltForEach),
        typeof(XsltForEachGroup), typeof(XsltIterate), typeof(XsltAttributeValueTemplate), typeof(AvtExpression),
    };

    [Theory]
    [MemberData(nameof(Copied))]
    public void A_copy_keeps_every_other_field(Type type)
    {
        var original = RuntimeHelpers.GetUninitializedObject(type);
        var fields = Fields(type);
        foreach (var field in fields)
            field.SetValue(original, Sample(field.FieldType));
        var with = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.DeclaringType == type && m.Name.StartsWith("With", StringComparison.Ordinal));
        var parameters = with.GetParameters();
        var args = parameters.Select(p => Sample(p.ParameterType)).ToArray();

        var copy = with.Invoke(original, args)!;

        copy.GetType().Should().Be(type);
        fields.Should().NotBeEmpty();
        foreach (var field in fields)
        {
            var property = PropertyOf(field);
            var at = Array.FindIndex(parameters, p => string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase));
            var expected = at >= 0 ? args[at] : field.GetValue(original);
            var actual = field.GetValue(copy);
            if (field.FieldType.IsValueType)
                actual.Should().Be(expected, $"{type.Name}.{property} must be {(at >= 0 ? "the operand given" : "copied")}");
            else
                actual.Should().BeSameAs(expected, $"{type.Name}.{property} must be {(at >= 0 ? "the operand given" : "copied")}");
        }
    }

    private static List<FieldInfo> Fields(Type type)
    {
        var fields = new List<FieldInfo>();
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        return fields;
    }

    private static string PropertyOf(FieldInfo field)
        => field.Name.StartsWith('<') ? field.Name[1..field.Name.IndexOf('>', StringComparison.Ordinal)] : field.Name;

    private static int s_next;

    // A value of the type that is not its default, and a new object for a reference type.
    private static object? Sample(Type type)
    {
        var n = Interlocked.Increment(ref s_next);
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string)) return "s" + n;
        if (underlying == typeof(bool)) return true;
        if (underlying == typeof(int)) return n;
        if (underlying == typeof(double)) return n + 0.5;
        if (underlying.IsEnum) return Enum.GetValues(underlying).GetValue(Enum.GetValues(underlying).Length - 1);
        if (underlying == typeof(QName)) return new QName(NamespaceId.None, "q" + n);
        if (underlying == typeof(Uri)) return new Uri("urn:s:" + n);
        if (underlying.IsValueType) return Activator.CreateInstance(underlying);
        if (underlying.IsGenericType && underlying.IsInterface)
        {
            var args = underlying.GetGenericArguments();
            var concrete = args.Length == 2 ? typeof(Dictionary<,>).MakeGenericType(args) : typeof(List<>).MakeGenericType(args);
            return Activator.CreateInstance(concrete);
        }
        if (underlying.IsAbstract || underlying.IsInterface)
        {
            var concrete = underlying.Assembly.GetTypes().First(t => underlying.IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
            return RuntimeHelpers.GetUninitializedObject(concrete);
        }
        return underlying.GetConstructor(Type.EmptyTypes) is not null && !underlying.IsGenericType
            ? RuntimeHelpers.GetUninitializedObject(underlying)
            : underlying.IsGenericType ? Activator.CreateInstance(underlying) : RuntimeHelpers.GetUninitializedObject(underlying);
    }
}
