using System.Text;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public class CharacterEscaperTests
{
    [Fact]
    public void XmlText_escapes_amp_lt_gt_only()
    {
        Assert.Equal("a&amp;b&lt;c&gt;d\"e\tf", CharacterEscaper.EscapeXmlText("a&b<c>d\"e\tf"));
    }

    [Fact]
    public void XmlAttribute_escapes_markup_quote_and_whitespace_as_char_refs()
    {
        var result = CharacterEscaper.EscapeXmlAttribute("a&b<c>d\"e\tf\ng\rh");
        Assert.Equal("a&amp;b&lt;c&gt;d&quot;e&#x9;f&#xA;g&#xD;h", result);
    }

    [Fact]
    public void XmlAttribute_append_matches_string_form()
    {
        var sb = new StringBuilder();
        CharacterEscaper.AppendXmlAttribute(sb, "x\ty\nz");
        Assert.Equal("x&#x9;y&#xA;z", sb.ToString());
    }
}
