<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10"/>
  <xsl:template name="xsl:initial-template"><data-element a-long-attribute-name1="&amp;zb&#10;z&amp; z&amp;xécx&#9;" v1="&#9;cyab&lt;aéabé&#10;&lt;x" a-long-attribute-name3="&#9;&quot;aé&quot;&#9;'&#9;x&quot;&amp;xéb&quot;éy&quot;b&lt;&amp;&#9;&#9;&#10;ba&lt;bx&quot;" type3="éx&quot;&amp;b" type2="&#10;&#10;éc&lt;&amp;z &gt;&lt;béx ba&#9;"></data-element></xsl:template>
</xsl:stylesheet>
