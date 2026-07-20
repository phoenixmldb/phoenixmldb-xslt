using System.Text;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public class StringOutputSinkTests
{
    [Fact]
    public void EmitsElementWithAttributeTextAndEnd()
    {
        var sb = new StringBuilder();
        var sink = new StringOutputSink(sb);
        sink.StartElementOpen("a");
        sink.Attribute("x", "1&2");
        sink.StartElementClose(selfClose: false);
        sink.Text("he<llo");
        sink.EndElement("a");
        Assert.Equal("<a x=\"1&amp;2\">he&lt;llo</a>", sb.ToString());
    }

    [Fact]
    public void EmitsSelfClosingElementWithNamespace()
    {
        var sb = new StringBuilder();
        var sink = new StringOutputSink(sb);
        sink.StartElementOpen("p:a");
        sink.Namespace("p", "urn:x");
        sink.StartElementClose(selfClose: true);
        Assert.Equal("<p:a xmlns:p=\"urn:x\"/>", sb.ToString());
    }

    [Fact]
    public void UndeclaresDefaultNamespaceWithEmptyUri()
    {
        var sb = new StringBuilder();
        var sink = new StringOutputSink(sb);
        sink.StartElementOpen("a");
        sink.Namespace("", "");
        sink.StartElementClose(selfClose: true);
        Assert.Equal("<a xmlns=\"\"/>", sb.ToString());
    }

    [Fact]
    public void EmitsCommentAndProcessingInstruction()
    {
        var sb = new StringBuilder();
        var sink = new StringOutputSink(sb);
        sink.Comment("hello");
        sink.ProcessingInstruction("target", "data");
        sink.ProcessingInstruction("target2", "");
        Assert.Equal("<!--hello--><?target data?><?target2?>", sb.ToString());
    }

    [Fact]
    public void EmitsRawTextVerbatim()
    {
        var sb = new StringBuilder();
        var sink = new StringOutputSink(sb);
        sink.RawText("<raw>&unescaped</raw>");
        Assert.Equal("<raw>&unescaped</raw>", sb.ToString());
    }
}
