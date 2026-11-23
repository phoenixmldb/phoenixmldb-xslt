<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" omit-xml-declaration="yes"/>
  <xsl:template name="xsl:initial-template"><section xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" xmlns="http://www.w3.org/1999/xhtml" type0="&#9;&gt;éa&amp;zéa&quot;éb&#10;&quot;&gt;&lt;&lt;x&quot;&quot;é'éz 'é&lt;" long-prefix:n0="a&gt;&#9;&quot;&lt; &lt;z&amp;&lt;c&lt; "><xsl:comment>é	ya
&gt;&amp;</xsl:comment></section></xsl:template>
</xsl:stylesheet>
