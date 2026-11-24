<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" html-version="5"/>
  <xsl:template name="xsl:initial-template"><html xmlns="http://www.w3.org/1999/xhtml"><item a-long-attribute-name2="zb'&lt;b&quot;&amp;éc&gt;x'zéa&quot;&amp;&gt;&lt;" a-long-attribute-name0="&gt;&quot;&gt;yzc &lt;&lt;é"></item></html></xsl:template>
</xsl:stylesheet>
