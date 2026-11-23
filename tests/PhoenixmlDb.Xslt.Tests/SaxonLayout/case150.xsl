<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" cdata-section-elements="p"/>
  <xsl:template name="xsl:initial-template"><p xmlns="http://www.w3.org/1999/xhtml" v3="&quot;'&#10;'&gt;&#9;''&lt;b  &lt;&amp;zz&#9;&#9;" type3="a''bé &gt;&amp;&#9;y &quot;"></p></xsl:template>
</xsl:stylesheet>
