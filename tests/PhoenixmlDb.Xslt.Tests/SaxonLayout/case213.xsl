<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" html-version="4.0"/>
  <xsl:template name="xsl:initial-template"><title xmlns="http://www.w3.org/1999/xhtml" a-long-attribute-name1="é&amp;" id1="zy&#10;x&#9;"><xsl:text>
éc&gt;'x&gt;yx</xsl:text><xsl:text>   </xsl:text></title></xsl:template>
</xsl:stylesheet>
