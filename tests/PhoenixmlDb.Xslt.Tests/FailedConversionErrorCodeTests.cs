using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An untyped value that the declared type's cast rejects is a type error with the code of the
/// binding: XTTE0570 for a variable, XTTE0590 for a global or template parameter's value, XTTE0600
/// for a template parameter's default or an xsl:iterate parameter's initial value (XSLT 3.0 §9.3).
/// A derived type (xs:int, xs:NCName, xs:dateTimeStamp) raised the cast's FORG0001 at every
/// binding, and an xsl:iterate initial value was not converted or checked at all (3.2.0 accepts
/// as="xs:integer" over "abc" there). Every expected code is Saxon-HE 12.10's.
/// </summary>
public class FailedConversionErrorCodeTests
{
    private static async Task<string> Run(string declarations, string body, string attribute)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:output method="text"/>
              <xsl:variable name="d" select="parse-xml('&lt;r a=&quot;{attribute}&quot;/&gt;')"/>
              {declarations}
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync((string?)null);
    }

    public static TheoryData<string, string, string, string> Cases => new()
    {
        { "xs:integer gvar", """<xsl:variable name="v" as="xs:integer" select="$d/r/@a"/>""", """<xsl:value-of select="$v"/>""", "abc|XTTE0570" },
        { "xs:integer gparam", """<xsl:param name="v" as="xs:integer" select="$d/r/@a"/>""", """<xsl:value-of select="$v"/>""", "abc|XTTE0590" },
        { "xs:integer lvar", "", """<xsl:variable name="v" as="xs:integer" select="$d/r/@a"/><xsl:value-of select="$v"/>""", "abc|XTTE0570" },
        { "xs:integer tparam", """<xsl:template name="t"><xsl:param name="p" as="xs:integer"/><xsl:value-of select="$p"/></xsl:template>""", """<xsl:call-template name="t"><xsl:with-param name="p" select="$d/r/@a"/></xsl:call-template>""", "abc|XTTE0590" },
        { "xs:integer tdefault", """<xsl:template name="t"><xsl:param name="p" as="xs:integer" select="$d/r/@a"/><xsl:value-of select="$p"/></xsl:template>""", """<xsl:call-template name="t"/>""", "abc|XTTE0600" },
        { "xs:integer iterate", "", """<xsl:iterate select="1"><xsl:param name="p" as="xs:integer" select="$d/r/@a"/><xsl:value-of select="$p"/></xsl:iterate>""", "abc|XTTE0600" },
        { "xs:date gvar", """<xsl:variable name="v" as="xs:date" select="$d/r/@a"/>""", """<xsl:value-of select="$v"/>""", "x|XTTE0570" },
        { "xs:date gparam", """<xsl:param name="v" as="xs:date" select="$d/r/@a"/>""", """<xsl:value-of select="$v"/>""", "x|XTTE0590" },
        { "xs:date lvar", "", """<xsl:variable name="v" as="xs:date" select="$d/r/@a"/><xsl:value-of select="$v"/>""", "x|XTTE0570" },
        { "xs:date tparam", """<xsl:template name="t"><xsl:param name="p" as="xs:date"/><xsl:value-of select="$p"/></xsl:template>""", """<xsl:call-template name="t"><xsl:with-param name="p" select="$d/r/@a"/></xsl:call-template>""", "x|XTTE0590" },
        { "xs:date tdefault", """<xsl:template name="t"><xsl:param name="p" as="xs:date" select="$d/r/@a"/><xsl:value-of select="$p"/></xsl:template>""", """<xsl:call-template name="t"/>""", "x|XTTE0600" },
        { "xs:date iterate", "", """<xsl:iterate select="1"><xsl:param name="p" as="xs:date" select="$d/r/@a"/><xsl:value-of select="$p"/></xsl:iterate>""", "x|XTTE0600" },
        { "xs:boolean gvar", """<xsl:variable name="v" as="xs:boolean" select="$d/r/@a"/>""", """<xsl:value-of select="$v"/>""", "maybe|XTTE0570" },
        { "xs:boolean gparam", """<xsl:param name="v" as="xs:boolean" select="$d/r/@a"/>""", """<xsl:value-of select="$v"/>""", "maybe|XTTE0590" },
        { "xs:boolean lvar", "", """<xsl:variable name="v" as="xs:boolean" select="$d/r/@a"/><xsl:value-of select="$v"/>""", "maybe|XTTE0570" },
        { "xs:boolean tparam", """<xsl:template name="t"><xsl:param name="p" as="xs:boolean"/><xsl:value-of select="$p"/></xsl:template>""", """<xsl:call-template name="t"><xsl:with-param name="p" select="$d/r/@a"/></xsl:call-template>""", "maybe|XTTE0590" },
        { "xs:boolean tdefault", """<xsl:template name="t"><xsl:param name="p" as="xs:boolean" select="$d/r/@a"/><xsl:value-of select="$p"/></xsl:template>""", """<xsl:call-template name="t"/>""", "maybe|XTTE0600" },
        { "xs:boolean iterate", "", """<xsl:iterate select="1"><xsl:param name="p" as="xs:boolean" select="$d/r/@a"/><xsl:value-of select="$p"/></xsl:iterate>""", "maybe|XTTE0600" },
        { "xs:int gvar", """<xsl:variable name="v" as="xs:int" select="$d/r/@a"/>""", """<xsl:value-of select="$v"/>""", "99999999999|XTTE0570" },
        { "xs:int gparam", """<xsl:param name="v" as="xs:int" select="$d/r/@a"/>""", """<xsl:value-of select="$v"/>""", "99999999999|XTTE0590" },
        { "xs:int lvar", "", """<xsl:variable name="v" as="xs:int" select="$d/r/@a"/><xsl:value-of select="$v"/>""", "99999999999|XTTE0570" },
        { "xs:int tparam", """<xsl:template name="t"><xsl:param name="p" as="xs:int"/><xsl:value-of select="$p"/></xsl:template>""", """<xsl:call-template name="t"><xsl:with-param name="p" select="$d/r/@a"/></xsl:call-template>""", "99999999999|XTTE0590" },
        { "xs:int tdefault", """<xsl:template name="t"><xsl:param name="p" as="xs:int" select="$d/r/@a"/><xsl:value-of select="$p"/></xsl:template>""", """<xsl:call-template name="t"/>""", "99999999999|XTTE0600" },
        { "xs:int iterate", "", """<xsl:iterate select="1"><xsl:param name="p" as="xs:int" select="$d/r/@a"/><xsl:value-of select="$p"/></xsl:iterate>""", "99999999999|XTTE0600" },
        { "xs:dateTimeStamp gvar", """<xsl:variable name="v" as="xs:dateTimeStamp" select="$d/r/@a"/>""", """<xsl:value-of select="$v"/>""", "2026-01-01T00:00:00|XTTE0570" },
        { "xs:dateTimeStamp gparam", """<xsl:param name="v" as="xs:dateTimeStamp" select="$d/r/@a"/>""", """<xsl:value-of select="$v"/>""", "2026-01-01T00:00:00|XTTE0590" },
        { "xs:dateTimeStamp lvar", "", """<xsl:variable name="v" as="xs:dateTimeStamp" select="$d/r/@a"/><xsl:value-of select="$v"/>""", "2026-01-01T00:00:00|XTTE0570" },
        { "xs:dateTimeStamp tparam", """<xsl:template name="t"><xsl:param name="p" as="xs:dateTimeStamp"/><xsl:value-of select="$p"/></xsl:template>""", """<xsl:call-template name="t"><xsl:with-param name="p" select="$d/r/@a"/></xsl:call-template>""", "2026-01-01T00:00:00|XTTE0590" },
        { "xs:dateTimeStamp tdefault", """<xsl:template name="t"><xsl:param name="p" as="xs:dateTimeStamp" select="$d/r/@a"/><xsl:value-of select="$p"/></xsl:template>""", """<xsl:call-template name="t"/>""", "2026-01-01T00:00:00|XTTE0600" },
        { "xs:dateTimeStamp iterate", "", """<xsl:iterate select="1"><xsl:param name="p" as="xs:dateTimeStamp" select="$d/r/@a"/><xsl:value-of select="$p"/></xsl:iterate>""", "2026-01-01T00:00:00|XTTE0600" },
        { "xs:NCName gvar", """<xsl:variable name="v" as="xs:NCName" select="$d/r/@a"/>""", """<xsl:value-of select="$v"/>""", "1a|XTTE0570" },
        { "xs:NCName gparam", """<xsl:param name="v" as="xs:NCName" select="$d/r/@a"/>""", """<xsl:value-of select="$v"/>""", "1a|XTTE0590" },
        { "xs:NCName lvar", "", """<xsl:variable name="v" as="xs:NCName" select="$d/r/@a"/><xsl:value-of select="$v"/>""", "1a|XTTE0570" },
        { "xs:NCName tparam", """<xsl:template name="t"><xsl:param name="p" as="xs:NCName"/><xsl:value-of select="$p"/></xsl:template>""", """<xsl:call-template name="t"><xsl:with-param name="p" select="$d/r/@a"/></xsl:call-template>""", "1a|XTTE0590" },
        { "xs:NCName tdefault", """<xsl:template name="t"><xsl:param name="p" as="xs:NCName" select="$d/r/@a"/><xsl:value-of select="$p"/></xsl:template>""", """<xsl:call-template name="t"/>""", "1a|XTTE0600" },
        { "xs:NCName iterate", "", """<xsl:iterate select="1"><xsl:param name="p" as="xs:NCName" select="$d/r/@a"/><xsl:value-of select="$p"/></xsl:iterate>""", "1a|XTTE0600" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_value_the_cast_rejects_is_the_type_error_of_the_binding(string label, string declarations, string body, string attributeAndCode)
    {
        _ = label;
        var (attribute, code) = (attributeAndCode.Split('|')[0], attributeAndCode.Split('|')[1]);
        var act = () => Run(declarations, body, attribute);

        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        (ex is PhoenixmlDb.XQuery.Execution.XQueryRuntimeException rt ? rt.ErrorCode : ex.Message).Should().Contain(code);
    }
}
