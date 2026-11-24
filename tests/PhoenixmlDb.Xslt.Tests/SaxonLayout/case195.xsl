<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" html-version="5"/>
  <xsl:template name="xsl:initial-template"><br xmlns="http://www.w3.org/1999/xhtml" a-long-attribute-name1="" v1="&#10;cc&amp;'b' ya&lt;&#9;&amp;y&amp;&#10;x'  &#10;&quot;éz" a-long-attribute-name3="&amp;x&amp;z&gt;&#9;'a&#10;" v2="x&gt;z&quot;&amp;y &gt;c&quot;xy&#9;&lt;  &gt;a'" a-long-attribute-name2="yyca c"><img a-long-attribute-name0="&#9;&#10;&lt; 'y&quot;&gt;&gt;é&#10;&#10;"></img><xsl:text>&amp;bc</xsl:text><xsl:comment>


y&gt;é</xsl:comment></br></xsl:template>
</xsl:stylesheet>
