<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" omit-xml-declaration="yes"/>
  <xsl:template name="xsl:initial-template"><b xmlns="http://www.w3.org/1999/xhtml"><xsl:text>   </xsl:text><xsl:text>
</xsl:text><p xmlns:q="urn:example:q" id0="&lt;&gt;" q:id2="'cybb &quot;&quot; &gt; a&#9;z&#10;xé&amp;&#10;&lt; xz&#10;éa&quot; x&lt;"></p><xsl:comment>a</xsl:comment></b></xsl:template>
</xsl:stylesheet>
