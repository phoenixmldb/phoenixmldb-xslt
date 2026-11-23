<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10" doctype-system="case.dtd"/>
  <xsl:template name="xsl:initial-template"><section n2="&#10;&lt;'&lt;é'a&gt; &gt;aa&lt;cz&gt;'z&amp;&#10;z&lt;c&lt;az&quot;" id1="x'" type1="a&gt;y&#9;é'yé&quot;&lt;z&quot;az&#10;&gt;'&#9;az&#10;&#9;bczy" a-long-attribute-name3="'éy&amp;b&quot;z'&gt;zy'&amp;xyabc&#9;ya&#9;z&lt;c&quot;"><data-element></data-element><xsl:text>   </xsl:text><xsl:comment>x</xsl:comment></section></xsl:template>
</xsl:stylesheet>
