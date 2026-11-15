using System.Text;
using AwesomeAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The result writer (#400) gives the same text whatever size of piece each stage reads at a
/// time. The markup is random: start, end and
/// empty tags of html, svg, prefixed and other elements, heads with and without a Content-Type
/// meta, text with spaces, newlines and '&gt;', comments with tags in them, processing
/// instructions, a DOCTYPE, and end tags that close nothing.
/// </summary>
#pragma warning disable CA5394 // a seeded Random makes the samples repeatable; nothing here is security
public sealed class ResultWriterTests
{
    private static readonly string[] Names =
        ["html", "head", "HEAD", "Head", "header", "body", "title", "p", "div", "br", "meta", "span", "h1",
         "ul", "li", "pre", "table", "td", "script", "svg:svg", "svg:title", "h:p", "h:head", "h:br", "x:y", "m:math"];

    private static readonly string[] Attributes =
        [" a=\"1\"", " class='c d'", " xmlns:h=\"http://www.w3.org/1999/xhtml\"", " xmlns:svg=\"http://www.w3.org/2000/svg\"",
         " xmlns:m=\"http://www.w3.org/1998/Math/MathML\"", " xmlns:x=\"urn:x\"", " xmlns=\"http://www.w3.org/1999/xhtml\"",
         " xmlns=\"\"", " http-equiv=\"Content-Type\" content=\"text/plain\"", " h:lang=\"en\"", " x:a=\"&gt;\""];

    private static readonly string[] Texts =
        ["x", "hello world", " ", "\n", "\n    ", "  a  ", "a &amp; b", "a > b", "\t", "&#160;", "]]&gt;"];

    private static string Markup(Random r)
    {
        var sb = new StringBuilder();
        var open = new Stack<string>();
        if (r.Next(4) == 0) sb.Append("<!DOCTYPE html>");
        var n = r.Next(1, 40);
        for (var k = 0; k < n; k++)
        {
            switch (r.Next(10))
            {
                case 0 or 1 or 2:
                {
                    var name = Names[r.Next(Names.Length)];
                    sb.Append('<').Append(name);
                    for (var a = r.Next(3); a > 0; a--)
                        sb.Append(Attributes[r.Next(Attributes.Length)]);
                    if (r.Next(3) == 0)
                        sb.Append(r.Next(2) == 0 ? "/>" : " />");
                    else
                    {
                        sb.Append('>');
                        open.Push(name);
                    }
                    break;
                }
                case 3 or 4:
                    if (open.Count > 0) sb.Append("</").Append(open.Pop()).Append('>');
                    break;
                case 5:
                    sb.Append("</").Append(Names[r.Next(Names.Length)]).Append('>');
                    break;
                case 6 or 7:
                    sb.Append(Texts[r.Next(Texts.Length)]);
                    break;
                case 8:
                    sb.Append(r.Next(2) == 0 ? "<!-- <br/> <head> c -->" : "<?pi a > b?>");
                    break;
                default:
                    sb.Append("<meta http-equiv=\"Content-Type\" content=\"x\"/>");
                    break;
            }
        }
        while (open.Count > 0 && r.Next(5) != 0)
            sb.Append("</").Append(open.Pop()).Append('>');
        return sb.ToString();
    }

    public static TheoryData<string, bool, bool, string> Configurations => new()
    {
        { "html", true, true, "" }, { "html", true, false, "" }, { "html", false, true, "" },
        { "html", true, true, "p pre" }, { "html", true, true, "head title" },
        { "xhtml", true, true, "" }, { "xhtml", true, false, "" }, { "xhtml", false, true, "" },
        { "xhtml", true, true, "p" }, { "xml", false, true, "" }, { "xml", false, true, "p" },
    };

    private static ResultWriter.Steps StepsFor(string method, bool meta, bool indent, string suppress)
    {
        var m = method switch { "html" => OutputMethod.Html, "xhtml" => OutputMethod.Xhtml, _ => OutputMethod.Xml };
        var html = m != OutputMethod.Xml;
        HashSet<QName>? names = suppress.Length == 0 ? null
            : [.. suppress.Split(' ').Select(local => new QName(NamespaceId.None, local))];
        return new ResultWriter.Steps(m, html, html, meta && html ? new XsltOutput { Method = m } : null, indent, names);
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void Every_piece_size_gives_the_same_text(string method, bool meta, bool indent, string suppress)
    {
        var steps = StepsFor(method, meta, indent, suppress);
        var r = new Random(4000 + method.Length * 31 + suppress.Length);
        for (var sample = 0; sample < 400; sample++)
        {
            var markup = Markup(r);
            var whole = ResultWriter.Write(markup, steps, chunk: 0);
            foreach (var chunk in (int[])[1, 2, 3, 7, 17])
                ResultWriter.Write(markup, steps, chunk).Should().Be(whole, $"chunk {chunk} of {markup}");
        }
    }
}
