<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" omit-xml-declaration="yes" doctype-system="case.dtd"/>
  <xsl:template name="xsl:initial-template"><script xmlns="http://www.w3.org/1999/xhtml" a-long-attribute-name1="y'éaca y&lt;z&lt;y&amp;&lt;&gt;&lt;'&quot;ca &amp;x&quot;&gt;x" type3="a&amp;&gt;&quot;'&gt;&lt;éb&#9;x&#9;&#9;&lt;'yaz''&amp;"><xsl:text></xsl:text></script></xsl:template>
</xsl:stylesheet>
