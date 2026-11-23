<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10" cdata-section-elements="e"/>
  <xsl:template name="xsl:initial-template"><x xmlns:q="urn:example:q"><e n2="x'zééé&quot;&amp;&#10;cbc&#9;&gt;czz&quot;&amp;  cé&lt;y" n3="éac&quot;a&gt;xxc c" q:n2="é&#9;&amp;'&lt;a&#9;c&amp;&amp;yzéa "><xsl:comment>zc</xsl:comment></e></x></xsl:template>
</xsl:stylesheet>
