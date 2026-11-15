using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The html method's indentation puts no whitespace into inline content, and writes boolean
/// attributes in minimized form, as Saxon-HE 12.10 does.
/// <list type="bullet">
/// <item>br is inline: <c>a&lt;br&gt;b</c> stays on one line (it was a "block", so a newline
/// went before it and added a space to the text; W3C include-0101).</item>
/// <item>No newline between the tag of an inline element and a block tag
/// (<c>&lt;span&gt;s&lt;/span&gt;&lt;p&gt;</c>; W3C attribute-0701).</item>
/// <item>An element that is not an HTML inline element, one the list does not know or in
/// another namespace, is a block.</item>
/// <item><c>checked="checked"</c> on input is written <c>checked</c>; which attributes are
/// boolean depends on the element. Not with the xhtml method.</item>
/// </list>
/// </summary>
public sealed class HtmlInlineIndentationTests
{
    private static async Task<string> Run(string body, string method = "html", string indent = "yes")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="{{method}}" indent="{{indent}}" include-content-type="no"/>
              <xsl:template name="xsl:initial-template"><html><body>{{body}}</body></html></xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("<p>a<br/>b</p>", "<p>a<br>b</p>")]
    [InlineData("<div>t<br/></div>", "<div>t<br></div>")]
    [InlineData("<div><span>s</span><p>q</p></div>", "<div><span>s</span><p>q</p>\n    </div>")]
    [InlineData("<form>Zone<input type='checkbox'/></form>", "<form>Zone<input type=\"checkbox\"></form>")]
    public async Task Inline_content_gets_no_whitespace(string body, string expected)
        => (await Run(body)).Should().Contain(expected);

    [Theory]
    [InlineData("<div><p>a</p><foo>x</foo></div>", "</p>\n      <foo>x</foo>\n    </div>")]
    [InlineData("<div><p>a</p><x:e xmlns:x='urn:x'>x</x:e></div>", "</p>\n      <x:e xmlns:x=\"urn:x\">x</x:e>\n    </div>")]
    public async Task An_element_that_is_not_inline_is_a_block(string body, string expected)
        => (await Run(body)).Should().Contain(expected);

    [Theory]
    [InlineData("<input checked='checked'/>", "<input checked>")]
    [InlineData("<Input Type='checkbox' CHECKED='CHECKED'/>", "<Input Type=\"checkbox\" CHECKED>")]
    [InlineData("<input disabled='Disabled' value='v'/>", "<input disabled value=\"v\">")]
    [InlineData("<option selected='selected'>o</option>", "<option selected>o</option>")]
    [InlineData("<div hidden='hidden'/>", "<div hidden></div>")]
    [InlineData("<div checked='checked'/>", "<div checked=\"checked\"></div>")]
    [InlineData("<input checked='yes'/>", "<input checked=\"yes\">")]
    [InlineData("<td nowrap='nowrap'>x</td>", "<td nowrap>x</td>")]
    public async Task A_boolean_attribute_is_minimized(string body, string expected)
        => (await Run(body, indent: "no")).Should().Contain(expected);

    [Fact]
    public async Task The_xhtml_method_does_not_minimize()
        => (await Run("<input checked='checked'/>", method: "xhtml", indent: "no")).Should().Contain("<input checked=\"checked\" />");
}
