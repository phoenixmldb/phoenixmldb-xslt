<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" html-version="5"/>
  <xsl:template name="xsl:initial-template"><html xmlns="http://www.w3.org/1999/xhtml"><li xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" type3=" &quot;&lt;&amp;y&#10;z a" long-prefix:type2="&#10;" a-long-attribute-name1="&quot;b&#10;&#10;'zéaabb é x'a&quot;ac"><b a-long-attribute-name3="'&#10;" v3="xy&#10;&lt;&#9;&#10;''&amp;é&gt;'&lt;zcabé&quot;&amp;&#9;&lt;&amp;xx&#9;xé "><xsl:text>
 bb&amp;</xsl:text><xsl:text></xsl:text><xsl:comment>&lt;a&gt;c&gt;	y	b</xsl:comment></b></li></html></xsl:template>
</xsl:stylesheet>
