<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10"/>
  <xsl:template name="xsl:initial-template"><section xmlns:q="urn:example:q" xmlns="urn:default" n1="&quot;b'yé&amp;é'&gt;&gt;xyxa&quot;&lt;&amp;x&amp;"><xsl:processing-instruction name="pi0"></xsl:processing-instruction></section></xsl:template>
</xsl:stylesheet>
