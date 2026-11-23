<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10" doctype-system="case.dtd"/>
  <xsl:template name="xsl:initial-template"><item n0="&gt;ééé  &#10;&lt;a&#10;xzb" type3="&amp;&lt;&#9;x'&amp;c&quot;&#9;é&amp;&#9;&quot;&gt; y &#9;&#10;&#10;&lt;é&#9;a" n1="" v2="&amp;a&gt;éy'xc&gt;z é&gt;&#10;&#10;'b'&#10;" id2="''&amp;' &amp;" a-long-attribute-name2="&amp;&#10;b xybyxxx&#9;éc&gt;é&quot;&#10;&#9;éb&amp;&gt;xa"></item></xsl:template>
</xsl:stylesheet>
