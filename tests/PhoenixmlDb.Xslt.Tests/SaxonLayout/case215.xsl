<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" html-version="1.0"/>
  <xsl:template name="xsl:initial-template"><html xmlns="http://www.w3.org/1999/xhtml"><img xmlns="http://www.w3.org/1999/xhtml" a-long-attribute-name2="a&lt;&#10;&#9;c&gt;&#9;x&gt;&quot;c&amp;&quot;x&lt;&lt;aaaé&gt;éx&lt;&#10;&lt;&#9;" type2="&quot; bzx&gt;&#9;éac''&#10;"><xsl:comment>x&lt;y	z"y&lt;b"</xsl:comment></img></html></xsl:template>
</xsl:stylesheet>
