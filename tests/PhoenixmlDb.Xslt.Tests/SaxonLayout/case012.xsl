<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10"/>
  <xsl:template name="xsl:initial-template"><item v3="'&gt;&lt;&quot;'x&quot;a&quot;a&lt;&gt;éxyb &#9;'x&quot;b" n0="éy&gt; &#10;&lt;é&#10;" n3="z&gt;&gt;b z&quot;&amp;&gt;&amp;x&gt;&quot;é&amp;c" a-long-attribute-name0="&quot;xzz&amp;'bcy b b&#10;&lt;yb"><xsl:text>
cayz &gt; z"	</xsl:text><xsl:text>yay'</xsl:text><xsl:comment>"z</xsl:comment></item></xsl:template>
</xsl:stylesheet>
