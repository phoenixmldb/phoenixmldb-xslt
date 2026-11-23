<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10" doctype-system="case.dtd" cdata-section-elements="data-element"/>
  <xsl:template name="xsl:initial-template"><data-element a-long-attribute-name2="&quot;&#9;y&#10;&#10;a&#9;xyz&lt;&#10;&#10;y&#10;y" a-long-attribute-name1="'&gt;c&#9;yéyxc &#10;éc béz éabz" id3="ayé&#9;&gt;"></data-element></xsl:template>
</xsl:stylesheet>
