<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10" cdata-section-elements="a"/>
  <xsl:template name="xsl:initial-template"><a><item xmlns:p="urn:p" type0="é z&#10;&amp;x&gt;&#10;y&#9;y " p:id2="&#9;'céz&gt;z&quot;c&gt;b" a-long-attribute-name3="&quot;'&gt;" n1="&#10;&#9;y&lt;&#10;&amp;" type1="' z&amp;&lt;y&#9;y&#10;&#10;' &quot;y&#9;c&quot;&#10;x&#10;&#10;&#9;"><xsl:text>a &lt;&gt; </xsl:text><item><p:item><xsl:processing-instruction name="pi0">b&amp;é'
a
"</xsl:processing-instruction><xsl:text>   </xsl:text><xsl:text>zézé
'y""z	
</xsl:text><xsl:comment>z
y&gt;ay&lt;</xsl:comment></p:item><p:b n3="&amp; '&#10; b&amp;x&#10;&gt;&amp;&gt;xé &gt;'&amp;&gt;" a-long-attribute-name1="ax"></p:b><xsl:text>   </xsl:text></item></item><item xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" long-prefix:n3="y&quot;&quot;&gt;z&#10;  &amp;&lt;b&#10;z&gt;c&quot;&lt;abé &gt;y&amp;yz&quot;" long-prefix:id0="&#10;&#10;zéxbc&lt;cyax&#9;z&gt;&lt;b&amp;yy'" type0=" " n1="x&lt;&quot;'cc&#10;xzb&quot; x&gt;yx&lt; &#10;y&#9;"><x xmlns:p="urn:p" v1="&#9;" a-long-attribute-name3="cé &gt;y" n1="&#10;&#10;"><long-prefix:b n2="b&gt;&#9;x'&amp;&gt;&#10;czc'&lt;c&#10;&amp;éz '&gt;&lt;&#10;bayé" v3="&lt;&amp;&#10;b&amp;ybc&amp;c&gt;&gt;&gt;&#10;&gt;&amp;yb&lt;y&amp;" v2="'&quot;&#9;&amp;" n1="" n0="&#10;c&#10;&#9;&lt;zx&lt;z&#9;z&amp;&#10;zc&quot;éb&quot;c'a&#9;&quot;b&lt;z'&lt;" id0="y&gt;byc'c&#10;&gt;&quot;  &lt;'&lt;bbc"></long-prefix:b></x></item></a></xsl:template>
</xsl:stylesheet>
