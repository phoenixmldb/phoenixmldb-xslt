<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" suppress-indentation="li" html-version="5"/>
  <xsl:template name="xsl:initial-template"><li xmlns="http://www.w3.org/1999/xhtml" a-long-attribute-name3="&lt;&#10; &#10;" a-long-attribute-name0="&#10;&gt;&#10;'&amp;&gt;cé&#9;x&#9;zc'"><title n2="'&#9;x '&#10;  &#9;ééx&#10;&#10;éx&quot;é&quot;cé" type3="ccx&quot;a &lt;&gt; &lt;&amp;"></title></li></xsl:template>
</xsl:stylesheet>
