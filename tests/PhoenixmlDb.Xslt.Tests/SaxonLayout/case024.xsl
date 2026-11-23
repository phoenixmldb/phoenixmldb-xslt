<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10" omit-xml-declaration="yes" doctype-system="case.dtd"/>
  <xsl:template name="xsl:initial-template"><x xmlns:p="urn:p" n0="é&#9;" p:v0="zb' z&lt;&quot;&quot; c&quot;&lt; c'&#10;zéx&#9; xz&gt;y" type1="x&#10;&gt;&#9;&lt;yz'&amp;c'xy&quot;&#10;&quot;z&lt;cxéb&amp;"></x></xsl:template>
</xsl:stylesheet>
