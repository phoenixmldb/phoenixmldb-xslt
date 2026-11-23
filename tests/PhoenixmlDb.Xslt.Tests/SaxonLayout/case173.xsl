<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10"/>
  <xsl:template name="xsl:initial-template"><script xmlns:p="urn:p" xmlns="http://www.w3.org/1999/xhtml" n3="a&#10;&#10;x'&lt;&gt;xéa&quot;bzéb&amp;&gt;zy&quot;&gt;''ca&quot;b&quot;"><xsl:text>x&amp;éz&amp;&lt;'
&gt;</xsl:text></script></xsl:template>
</xsl:stylesheet>
