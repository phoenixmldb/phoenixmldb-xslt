using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable xUnit1051 // CancellationToken in test methods — TransformAsync has optional CT

namespace PhoenixmlDb.Xslt.Tests;

public class NumberFromDebugTest
{
    private readonly ITestOutputHelper _output;

    public NumberFromDebugTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task NumberWithFromAttribute()
    {
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"" version=""2.0"">
<xsl:template match=""doc"">
  <out><xsl:apply-templates/></out>
</xsl:template>
<xsl:template match=""note"">
[<xsl:number level=""single"" from=""chapter""/>]<xsl:apply-templates/>
</xsl:template>
</xsl:stylesheet>";

        var xml = @"<?xml version=""1.0""?>
<doc>
  <note>aaa</note>
  <note>bbb</note>
  <chapter>
    <note>ddd</note>
    <note>eee</note>
  </chapter>
  <note>ggg</note>
</doc>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result);
        // Notes aaa,bbb should be 1,2 (no chapter ancestor)
        // Notes ddd,eee should be 1,2 (inside chapter)
        // Note ggg should be 3 (after chapter, continues doc-level numbering)
        result.Should().Contain("[1]aaa");
        result.Should().Contain("[2]bbb");
        result.Should().Contain("[1]ddd");
        result.Should().Contain("[2]eee");
        result.Should().Contain("[3]ggg");
    }

    [Fact]
    public async Task NumberWithSelectAttribute()
    {
        // Test the select attribute on xsl:number
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"" version=""2.0"">
<xsl:template match=""/"">
  <out>
    <xsl:text>ccc pos: </xsl:text><xsl:number select=""//note[.='ccc']""/>
    <xsl:text>, mmm pos: </xsl:text><xsl:number select=""//note[.='mmm']""/>
  </out>
</xsl:template>
</xsl:stylesheet>";

        var xml = @"<?xml version=""1.0""?>
<doc>
  <chapter>
    <note>aaa</note>
    <note>bbb</note>
    <note>ccc</note>
    <note>ddd</note>
    <note>eee</note>
    <note>fff</note>
    <note>ggg</note>
    <note>hhh</note>
    <note>iii</note>
    <note>jjj</note>
    <note>kkk</note>
    <note>lll</note>
    <note>mmm</note>
  </chapter>
</doc>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result);
        result.Should().Be("<out>ccc pos: 3, mmm pos: 13</out>");
    }

    [Fact]
    public async Task XPathPredicateFiltering()
    {
        // Test that XPath predicate [.='ccc'] correctly filters nodes
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"" version=""2.0"">
<xsl:template match=""/"">
  <out>
    <xsl:text>Selected: </xsl:text>
    <xsl:for-each select=""//note[.='ccc']"">
      <xsl:value-of select="".""/>
    </xsl:for-each>
  </out>
</xsl:template>
</xsl:stylesheet>";

        var xml = @"<?xml version=""1.0""?>
<doc>
  <chapter>
    <note>aaa</note>
    <note>bbb</note>
    <note>ccc</note>
    <note>ddd</note>
  </chapter>
</doc>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result);
        result.Should().Be("<out>Selected: ccc</out>");
    }

    [Fact]
    public async Task Number1701_CurrentInCountPattern()
    {
        // Test use of current() in the count pattern of xsl:number
        // count="*[name()=name(current())]/*" means: count children of elements
        // whose parent has the same name as the current node
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"" version=""2.0"">
<xsl:template match=""*"">
  <xsl:copy>
    <xsl:attribute name=""nr"">
      <!-- count nodes having the same name as their parent -->
      <xsl:number count=""*[name()=name(current())]/*"" level=""any""/>
    </xsl:attribute>
    <xsl:apply-templates/>
  </xsl:copy>
</xsl:template>
</xsl:stylesheet>";

        var xml = @"<doc><a><a/></a><a><b/></a><a><a/></a><a><b/><b/></a></doc>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result);
        // Expected: <doc nr=""><a nr=""><a nr="1"/></a><a nr="1"><b nr="1"/></a><a nr="1"><a nr="2"/></a><a nr="2"><b nr="2"/><b nr="2"/></a></doc>
        result.Should().Be(@"<doc nr=""""><a nr=""""><a nr=""1""/></a><a nr=""1""><b nr=""1""/></a><a nr=""1""><a nr=""2""/></a><a nr=""2""><b nr=""2""/><b nr=""2""/></a></doc>");
    }

    [Fact]
    public async Task CurrentFunctionBasic()
    {
        // Test that current() returns the outer XSLT context and
        // literal text between instructions is output correctly.
        // Uses xsl:text for precise whitespace control (the XSLT idiom).
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"" version=""2.0"">
<xsl:template match=""/"">
  <out>
    <xsl:for-each select=""//item"">
      <xsl:value-of select=""name(current())""/><xsl:text>:</xsl:text><xsl:value-of select=""current()""/><xsl:text>;</xsl:text>
    </xsl:for-each>
  </out>
</xsl:template>
</xsl:stylesheet>";

        var xml = @"<doc><item>A</item><item>B</item></doc>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result);
        result.Should().Be("<out>item:A;item:B;</out>");
    }

    [Fact]
    public async Task CurrentInPredicate()
    {
        // Test current() in a predicate
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"" version=""2.0"">
<xsl:template match=""/"">
  <out>
    <xsl:for-each select=""//item"">
      <!-- Select siblings with the same name as current() -->
      <xsl:value-of select=""count(//item[name()=name(current())])""/><xsl:text>;</xsl:text>
    </xsl:for-each>
  </out>
</xsl:template>
</xsl:stylesheet>";

        var xml = @"<doc><item>A</item><item>B</item></doc>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result);
        // There are 2 items named 'item', so count should be 2 for each
        result.Should().Be("<out>2;2;</out>");
    }

    [Fact]
    public async Task CurrentDiagnostic()
    {
        // More diagnostic test for current() behavior
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"" version=""2.0"">
<xsl:template match=""/"">
  <out>
    <xsl:for-each select=""//item"">
      <test>
        <curr-name><xsl:value-of select=""name(current())""/></curr-name>
        <curr-val><xsl:value-of select=""current()""/></curr-val>
        <dot-name><xsl:value-of select=""name(.)""/></dot-name>
        <dot-val><xsl:value-of select="".""/></dot-val>
        <are-same><xsl:value-of select=""current() is .""/></are-same>
        <count-all><xsl:value-of select=""count(//item)""/></count-all>
        <count-pred><xsl:value-of select=""count(//item[name()='item'])""/></count-pred>
        <count-curr><xsl:value-of select=""count(//item[name()=name(current())])""/></count-curr>
      </test>
    </xsl:for-each>
  </out>
</xsl:template>
</xsl:stylesheet>";

        var xml = @"<doc><item>A</item><item>B</item></doc>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result);
        // Check that current() matches the context in for-each
        result.Should().Contain("<curr-name>item</curr-name>");
        result.Should().Contain("<count-all>2</count-all>");
        result.Should().Contain("<count-pred>2</count-pred>");
        result.Should().Contain("<count-curr>2</count-curr>");
    }

    [Fact]
    public async Task BasicPredicateTest()
    {
        // Test that basic predicates work without current()
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"" version=""2.0"">
<xsl:template match=""/"">
  <out>
    <count-items><xsl:value-of select=""count(//item)""/></count-items>
    <count-with-pred><xsl:value-of select=""count(//item[name()='item'])""/></count-with-pred>
    <count-true><xsl:value-of select=""count(//item[true()])""/></count-true>
    <count-text><xsl:value-of select=""count(//item[.='A'])""/></count-text>
    <first-name><xsl:value-of select=""name(//item[1])""/></first-name>
    <first-val><xsl:value-of select=""//item[1]""/></first-val>
  </out>
</xsl:template>
</xsl:stylesheet>";

        var xml = @"<doc><item>A</item><item>B</item></doc>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result);
        result.Should().Contain("<count-items>2</count-items>");
        result.Should().Contain("<count-with-pred>2</count-with-pred>");
        result.Should().Contain("<count-true>2</count-true>");
        result.Should().Contain("<count-text>1</count-text>");
        result.Should().Contain("<first-name>item</first-name>");
        result.Should().Contain("<first-val>A</first-val>");
    }

    [Fact]
    public async Task NumberLevelAnySimple()
    {
        // Simplified test for level="any" counting
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"" version=""2.0"">
<xsl:template match=""*"">
  <xsl:copy>
    <xsl:attribute name=""n""><xsl:number count=""a"" level=""any""/></xsl:attribute>
    <xsl:apply-templates/>
  </xsl:copy>
</xsl:template>
</xsl:stylesheet>";

        var xml = @"<doc><a/><a/><a/></doc>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result);
        // doc has no count (not an <a>), first <a> is 1, second is 2, third is 3
        result.Should().Be(@"<doc n=""""><a n=""1""/><a n=""2""/><a n=""3""/></doc>");
    }

    [Fact]
    public async Task XslTextOnlyNewline()
    {
        // Test that xsl:text with only a newline outputs the newline
        // Use single-line format to avoid any ambiguity about whitespace
        var stylesheet = @"<?xml version=""1.0""?><xsl:stylesheet version=""2.0"" xmlns:xsl=""http://www.w3.org/1999/XSL/Transform""><xsl:template match=""/""><out><xsl:text>A</xsl:text><xsl:text>
</xsl:text><xsl:text>B</xsl:text></out></xsl:template></xsl:stylesheet>";

        var xml = @"<doc/>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result (raw): [" + result + "]");
        _output.WriteLine("Result: [" + result.Replace("\n", "\\n", StringComparison.Ordinal) + "]");
        // The middle xsl:text contains just a newline, so output should be "A\nB"
        result.Should().Contain("A\nB");
    }

    [Fact]
    public async Task XslTextWithNewlines()
    {
        // Test that xsl:text preserves newlines in output
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet version=""2.0"" xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"">
<xsl:template match=""/"">
  <out><xsl:text>Line1
Line2</xsl:text></out>
</xsl:template>
</xsl:stylesheet>";

        var xml = @"<doc/>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result.Replace("\n", "\\n", StringComparison.Ordinal));
        result.Should().Contain("Line1\nLine2");
    }

    [Fact]
    public async Task Number0818_SelectWithVariableTree()
    {
        // Test xsl:number level="any" with a count pattern that matches nodes in a variable tree
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet version=""2.0"" xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"">
  <xsl:template match=""/"">
    <xsl:variable name=""test"" as=""element()"">
      <a>
        <b/><b/><b/><c/>
      </a>
    </xsl:variable>
    <z><xsl:number level=""any"" select=""$test/c"" count=""a|b|c""/></z>
  </xsl:template>
</xsl:stylesheet>";

        var xml = @"<doc/>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result);
        // Expected: <z>5</z> - counting a (1) + b (2) + b (3) + b (4) + c (5)
        result.Should().Be("<z>5</z>");
    }

    [Fact]
    public async Task NumberLevelMultiple_3201()
    {
        // Test level="multiple" with count="a|b|c|d|e" - like number-3201
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"" version=""2.0"">
<xsl:template match=""doc"">
  <out><xsl:apply-templates/></out>
</xsl:template>
<xsl:template match=""title"">
    <xsl:number level=""multiple"" count=""a|b|c|d|e"" format=""1+1-1+1-1""/><xsl:text>: </xsl:text><xsl:value-of select="".""/>
</xsl:template>
<xsl:template match=""text()""/>
</xsl:stylesheet>";

        // Simplified XML structure
        var xml = @"<?xml version=""1.0""?>
<doc>
  <title>Test</title>
  <a>
    <title>Level A</title>
    <b>
      <title>Level B</title>
    </b>
  </a>
</doc>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result);
        // Expected: doc title has no ancestors matching count, so empty
        // First a/title -> "1: Level A" (1 ancestor 'a' at level 1)
        // First a/b/title -> "1+1: Level B" (ancestors are 'a' at level 1, 'b' at level 1 within a)
        result.Should().Contain("1: Level A");
        result.Should().Contain("1+1: Level B");
    }

    [Fact]
    public async Task NumberLevelMultiple_FullStructure()
    {
        // Full test like number-3201 with deeper nesting
        var stylesheet = @"<?xml version=""1.0""?>
<xsl:stylesheet xmlns:xsl=""http://www.w3.org/1999/XSL/Transform"" version=""2.0"">
<xsl:template match=""doc"">
  <out><xsl:apply-templates/></out>
</xsl:template>
<xsl:template match=""title"">
    <xsl:number level=""multiple"" count=""a|b|c|d|e"" format=""1+1-1+1-1""/><xsl:text>: </xsl:text><xsl:value-of select="".""/><xsl:text>
</xsl:text>
</xsl:template>
<xsl:template match=""text()""/>
</xsl:stylesheet>";

        var xml = @"<?xml version=""1.0""?>
<doc>
  <title>Test for source tree numbering</title>
  <a>
    <title>Level A</title>
    <b>
      <title>Level B</title>
    </b>
    <b>
      <title>Level B</title>
      <c>
        <title>Level C</title>
      </c>
    </b>
  </a>
</doc>";

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        var result = await transformer.TransformAsync(xml);
        _output.WriteLine("Result: " + result);
        // Check specific expected numbers
        result.Should().Contain(": Test for source tree numbering");  // No count ancestors
        result.Should().Contain("1: Level A");  // First 'a'
        result.Should().Contain("1+1: Level B"); // First 'a', first 'b'
        result.Should().Contain("1+2: Level B"); // First 'a', second 'b'
        result.Should().Contain("1+2-1: Level C"); // First 'a', second 'b', first 'c'
    }
}
