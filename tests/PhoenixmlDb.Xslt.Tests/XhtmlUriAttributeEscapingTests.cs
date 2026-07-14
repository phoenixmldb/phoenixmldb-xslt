using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// URI-attribute escaping for the XHTML output method (decl/output output-0102*/0103* slice).
/// The escape-uri-attributes serialization parameter (default "yes") governs how URI-valued
/// attributes (href, src, cite, action, ...) are serialized: when "yes", non-ASCII characters
/// are percent-encoded as their UTF-8 octets (after NFC normalization), including those the
/// XML serializer emitted as numeric character references (control chars); when "no", the
/// value is left to normal serialization. XML-significant characters stay XML-escaped and an
/// existing %xx sequence is never double-encoded.
/// </summary>
public class XhtmlUriAttributeEscapingTests
{
    private static async Task<string> Transform(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync("<in/>");
    }

    private const string Head =
        "<xsl:stylesheet version=\"3.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" " +
        "xmlns=\"http://www.w3.org/1999/xhtml\">";

    [Fact]
    public async Task Xhtml_EscapeYes_ControlCharReference_IsPercentEncoded()
    {
        // output-0102b: href="&#x96;" (U+0096 serialized as a numeric character reference)
        // must become %C2%96, not be left as a bare character reference.
        var ss = Head +
            "<xsl:output method=\"xhtml\" encoding=\"UTF-8\" escape-uri-attributes=\"yes\" indent=\"no\"/>" +
            "<xsl:template match=\"/\"><a href=\"&#x96;\"/></xsl:template></xsl:stylesheet>";
        var r = await Transform(ss);
        r.Should().Contain("href=\"%C2%96\"");
        r.Should().NotContain("&#x96;");
    }

    [Fact]
    public async Task Xhtml_EscapeYes_LiteralNonAscii_IsPercentEncoded()
    {
        // output-0102a: printable non-ASCII U+00A1 percent-encodes to %C2%A1.
        var ss = Head +
            "<xsl:output method=\"xhtml\" encoding=\"UTF-8\" escape-uri-attributes=\"yes\" indent=\"no\"/>" +
            "<xsl:template match=\"/\"><a href=\"&#xA1;\">t</a></xsl:template></xsl:stylesheet>";
        var r = await Transform(ss);
        r.Should().Contain("href=\"%C2%A1\"");
    }

    [Fact]
    public async Task Xhtml_EscapeNo_LiteralNonAscii_LeftRaw()
    {
        // output-0103a: escape-uri-attributes="no" leaves printable non-ASCII raw ("¡").
        var ss = Head +
            "<xsl:output method=\"xhtml\" encoding=\"UTF-8\" escape-uri-attributes=\"no\" indent=\"no\"/>" +
            "<xsl:template match=\"/\"><a href=\"&#xA1;\">t</a></xsl:template></xsl:stylesheet>";
        var r = await Transform(ss);
        r.Should().Contain("href=\"¡\"");
        r.Should().NotContain("%C2%A1");
    }

    [Fact]
    public async Task Xhtml_EscapeNo_ControlChar_LeftAsCharacterReference()
    {
        // output-0103b: escape-uri-attributes="no" leaves U+0096 as its character reference.
        var ss = Head +
            "<xsl:output method=\"xhtml\" encoding=\"UTF-8\" escape-uri-attributes=\"no\" indent=\"no\"/>" +
            "<xsl:template match=\"/\"><a href=\"&#x96;\"/></xsl:template></xsl:stylesheet>";
        var r = await Transform(ss);
        r.Should().MatchRegex("href=\"&#0*(150|x96);\"");
        r.Should().NotContain("%C2%96");
    }

    [Fact]
    public async Task Xhtml_EscapeYes_ExistingPercentEscape_NotDoubleEncoded()
    {
        // An already-percent-escaped %C2%96 must survive verbatim (no double-encoding of '%').
        var ss = Head +
            "<xsl:output method=\"xhtml\" encoding=\"UTF-8\" escape-uri-attributes=\"yes\" indent=\"no\"/>" +
            "<xsl:template match=\"/\"><a href=\"a%C2%96b\">t</a></xsl:template></xsl:stylesheet>";
        var r = await Transform(ss);
        r.Should().Contain("href=\"a%C2%96b\"");
        r.Should().NotContain("%25");
    }

    [Fact]
    public async Task Xhtml_EscapeYes_MixedContent_Matches0102c()
    {
        // output-0102c: mix of existing %xx, control-char ref, quote, non-ASCII, and XML-special
        // characters. Expected: '% %C2%96 %C2%96 a &#34;  %C2%A1 &lt; &gt; &amp; end'.
        var ss = Head +
            "<xsl:output method=\"xhtml\" encoding=\"UTF-8\" escape-uri-attributes=\"yes\" indent=\"no\"/>" +
            "<xsl:template match=\"/\"><a href='% %C2%96 &#x96; a \"  &#xA1; &lt; &gt; &amp; end'>t</a></xsl:template></xsl:stylesheet>";
        var r = await Transform(ss);
        r.Should().MatchRegex("href=\"% %C2%96 %C2%96 a &#(34|x22);  %C2%A1 &lt; &gt; &amp; end\"");
    }

    [Fact]
    public async Task Xhtml_EscapeYes_NonUriAttribute_Unaffected()
    {
        // Guard: a non-URI attribute (accesskey) is NOT percent-encoded (output-0102d).
        var ss = Head +
            "<xsl:output method=\"xhtml\" encoding=\"UTF-8\" escape-uri-attributes=\"yes\" indent=\"no\"/>" +
            "<xsl:template match=\"/\"><a accesskey=\"&#xA1;\">t</a></xsl:template></xsl:stylesheet>";
        var r = await Transform(ss);
        r.Should().Contain("accesskey=\"¡\"");
        r.Should().NotContain("%C2%A1");
    }

    [Fact]
    public async Task Xhtml_EscapeNo_UriAttributeQuote_IsNumericCharacterReference()
    {
        // output-0103c: escape-uri-attributes="no" leaves %xx, control-char refs and non-ASCII
        // raw, BUT a literal double quote inside a URI-valued attribute must be a NUMERIC character
        // reference (&#34; / &#x22;), never the named entity &quot;.
        var ss = Head +
            "<xsl:output method=\"xhtml\" encoding=\"UTF-8\" escape-uri-attributes=\"no\" indent=\"no\"/>" +
            "<xsl:template match=\"/\"><a href='% %C2%96 &#x96; a \"  &#xA1; &lt; &gt; &amp; end'>t</a></xsl:template></xsl:stylesheet>";
        var r = await Transform(ss);
        r.Should().MatchRegex("href=\"% %C2%96 &#0*(x96|150); a &#0*(34|x22);  ¡ &lt; &gt; &amp; end\"");
        r.Should().NotContain("&quot;");
    }

    [Fact]
    public async Task Xhtml_EscapeNo_OrdinaryAttributeQuote_StaysNamedEntity()
    {
        // Guard: a double quote in an ORDINARY (non-URI) attribute is unaffected by the URI-attribute
        // numeric-reference rule and continues to serialize as the named entity &quot;.
        var ss = Head +
            "<xsl:output method=\"xhtml\" encoding=\"UTF-8\" escape-uri-attributes=\"no\" indent=\"no\"/>" +
            "<xsl:template match=\"/\"><a title='x&quot;y'>t</a></xsl:template></xsl:stylesheet>";
        var r = await Transform(ss);
        r.Should().Contain("title=\"x&quot;y\"");
        r.Should().NotContain("&#34;");
        r.Should().NotContain("&#x22;");
    }

    [Fact]
    public async Task Xhtml_EscapeYes_XmlSpecialChars_StayEscaped()
    {
        // '<', '>', '&' inside a URI attribute remain XML-escaped, not percent-encoded.
        var ss = Head +
            "<xsl:output method=\"xhtml\" encoding=\"UTF-8\" escape-uri-attributes=\"yes\" indent=\"no\"/>" +
            "<xsl:template match=\"/\"><a href=\"a&lt;b&gt;c&amp;d\">t</a></xsl:template></xsl:stylesheet>";
        var r = await Transform(ss);
        r.Should().Contain("href=\"a&lt;b&gt;c&amp;d\"");
    }
}
