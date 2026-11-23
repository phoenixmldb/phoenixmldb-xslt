<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" doctype-system="case.dtd" doctype-public="-//PHX//CASE//EN"/>
  <xsl:template name="xsl:initial-template"><long-prefix:img xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" xmlns="http://www.w3.org/1999/xhtml" type2="&#9;aéc"></long-prefix:img></xsl:template>
</xsl:stylesheet>
