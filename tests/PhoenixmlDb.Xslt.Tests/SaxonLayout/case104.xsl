<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10"/>
  <xsl:template name="xsl:initial-template"><p:a xmlns:p="urn:p" id1=" &quot;xc&lt;&lt;'&amp;&gt;yé&gt;'é&gt;&quot;a&amp; x&#10;&amp;é&lt;&#10;ab"></p:a></xsl:template>
</xsl:stylesheet>
