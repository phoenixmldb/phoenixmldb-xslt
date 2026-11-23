<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10"/>
  <xsl:template name="xsl:initial-template"><e v1="cé'&gt; x" n3="''&#9;" type2="ca &lt;b ba&amp;'''b'xxéx&gt;y&quot;&#9;&quot;z&amp;y&#10;&amp; &#10;"></e></xsl:template>
</xsl:stylesheet>
