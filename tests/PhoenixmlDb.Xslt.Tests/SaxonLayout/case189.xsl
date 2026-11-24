<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" omit-xml-declaration="yes" html-version="5.0"/>
  <xsl:template name="xsl:initial-template"><p:section xmlns:p="urn:p" xmlns="http://www.w3.org/1999/xhtml" a-long-attribute-name2="&amp;z&gt;az&#10;&amp;" p:v2="" id3="&quot;"><xsl:text>   </xsl:text></p:section></xsl:template>
</xsl:stylesheet>
