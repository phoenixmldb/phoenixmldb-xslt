<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10" omit-xml-declaration="yes" doctype-system="case.dtd" doctype-public="-//PHX//CASE//EN"/>
  <xsl:template name="xsl:initial-template"><e a-long-attribute-name0="&amp;'é&gt;&#10;x'é'&quot;&#10;&#10;&lt;y&quot;z&#10;&gt; &#9;' éba&lt;y&#10;"></e></xsl:template>
</xsl:stylesheet>
