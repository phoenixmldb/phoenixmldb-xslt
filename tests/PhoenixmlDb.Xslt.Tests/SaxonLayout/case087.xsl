<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10"/>
  <xsl:template name="xsl:initial-template"><e v1="b&quot;zb&lt;b&#10; z&#9;bc&gt;x&gt;é"><data-element></data-element><xsl:comment>é&gt;&lt;y &lt;é&gt;é</xsl:comment><data-element xmlns="" v0="zz&#10;&amp;&amp;&#10;&#9;ca&lt;c"></data-element></e></xsl:template>
</xsl:stylesheet>
