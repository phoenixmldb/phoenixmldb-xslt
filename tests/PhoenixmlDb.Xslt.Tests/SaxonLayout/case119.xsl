<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10"/>
  <xsl:template name="xsl:initial-template"><long-prefix:data-element xmlns:long-prefix="urn:example:a-much-longer-namespace-uri"><xsl:text>	&amp;
b'xy  xb </xsl:text><xsl:processing-instruction name="pi2">b&amp;x&lt;"&amp;&gt;é</xsl:processing-instruction><xsl:processing-instruction name="pi1"> y	xz&gt;c&gt;</xsl:processing-instruction><xsl:text>b</xsl:text></long-prefix:data-element></xsl:template>
</xsl:stylesheet>
