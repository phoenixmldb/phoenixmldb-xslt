using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// HTML-method output is read as the result tree it serializes, so assertions can address it;
/// anything that is not HTML is left alone and still fails to parse when it is not XML.
/// </summary>
public class HtmlResultParsingTests
{
    [Fact]
    public void Void_elements_are_closed_and_the_tree_is_kept()
    {
        var xml = XsltTestRunner.HtmlAsXml(
            "<!DOCTYPE html><html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=UTF-8\"><title>T</title></head>"
            + "<body><p>a<br>b</p><img src='x.png' alt=\"a>b\"><nav><a href=\"n.html\">next</a></nav></body></html>");
        var doc = XDocument.Parse(xml!);
        doc.Descendants("a").Single().Attribute("href")!.Value.Should().Be("n.html");
        doc.Descendants("img").Single().Attribute("alt")!.Value.Should().Be("a>b");
        doc.Descendants("p").Single().Value.Should().Be("ab");
    }

    [Theory]
    [InlineData("<out><meta></out>")]   // not HTML: no html root
    [InlineData("plain text")]
    public void Anything_that_is_not_html_is_left_alone(string text) =>
        XsltTestRunner.HtmlAsXml(text).Should().BeNull();

    [Fact]
    public void Non_void_elements_are_not_repaired() =>
        FluentActions.Invoking(() => XDocument.Parse(XsltTestRunner.HtmlAsXml("<html><body><p>unclosed</body></html>")!))
            .Should().Throw<System.Xml.XmlException>();
}
