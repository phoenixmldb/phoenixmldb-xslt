<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10"/>
  <xsl:template name="xsl:initial-template"><div xmlns="http://www.w3.org/1999/xhtml" xml:space="preserve"><img v2="&gt;'a&lt;&gt;&lt;&amp;xyb  &amp;zb&#10;bxzy&amp;x&#10;c&quot;" n1=" &amp;&#10;&lt;&#10;'&gt;b&#9;&lt;&quot;&gt;&#10;&#9;&#9;b&lt;bzx&amp;x  z&quot;é"></img></div></xsl:template>
</xsl:stylesheet>
