<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10"/>
  <xsl:template name="xsl:initial-template"><item type0="&#9;'&quot;&amp;c&lt;&gt;zz  &quot;" type2="' ax&#10;' é" a-long-attribute-name3="cczz&quot;zbb&gt;cé&quot;" v2="c&lt;&lt;"><xsl:text>é	c</xsl:text><xsl:text>   </xsl:text></item></xsl:template>
</xsl:stylesheet>
