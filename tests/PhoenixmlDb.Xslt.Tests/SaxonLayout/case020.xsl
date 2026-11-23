<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10" doctype-system="case.dtd"/>
  <xsl:template name="xsl:initial-template"><a n0="&quot;' éz&#10;b&amp;yz&#9;c&#10;xa&amp;é x &#10;x&#9;" type0="&#10; &lt;"></a></xsl:template>
</xsl:stylesheet>
