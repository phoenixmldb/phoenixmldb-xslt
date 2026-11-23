<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10" omit-xml-declaration="yes"/>
  <xsl:template name="xsl:initial-template"><item id2="&gt;&quot;&quot;&quot;&amp;x&amp;c&#10;&gt;aé&quot;x&quot;&#10;&amp;&lt;&amp;x&#10; a&gt;&#10;" id1="c&amp;'é" a-long-attribute-name1="y&amp;a&#10;xb&amp;z  z"></item></xsl:template>
</xsl:stylesheet>
