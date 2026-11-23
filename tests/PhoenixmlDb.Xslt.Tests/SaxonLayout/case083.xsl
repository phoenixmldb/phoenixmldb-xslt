<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10" omit-xml-declaration="yes"/>
  <xsl:template name="xsl:initial-template"><item><xsl:text>&amp;' "b</xsl:text><a xmlns:p="urn:p"><data-element xmlns="urn:default" p:v1="a b&#10;&lt;&#10;&#10;&#10;yab&lt;" a-long-attribute-name3=" a&amp;z&#9;''bzz&#10;'&amp;aécyz&#10;&amp;cy&gt;a&quot;" p:type0="b"><xsl:text>"x"x&gt;a&amp;'&amp;zy</xsl:text><p:b xmlns:q="urn:example:q" id0="&#10;&#10;zca&lt;éy&quot;czz&#10;" a-long-attribute-name3=" a&#9;&#10;é'y'x&#10;a"><xsl:text>zxx&lt;
éz</xsl:text><xsl:text>   </xsl:text><x n0="&#9;" n1="&quot;&amp;&gt;x&quot;&#9;bxz&#10;&lt;" v0="&#9;yc&gt;&quot;é&#9;céab&lt;"></x><xsl:comment>&amp;x
</xsl:comment></p:b><xsl:text>&amp;c</xsl:text></data-element><xsl:processing-instruction name="pi0">yc	x&amp;cyx</xsl:processing-instruction><xsl:text>b&gt;bzé &gt;é	b"&lt;</xsl:text></a></item></xsl:template>
</xsl:stylesheet>
