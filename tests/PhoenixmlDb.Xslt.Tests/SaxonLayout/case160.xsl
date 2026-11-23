<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10"/>
  <xsl:template name="xsl:initial-template"><b xml:space="preserve"><xsl:processing-instruction name="pi2">&gt;</xsl:processing-instruction><SPAN xmlns:p="urn:p" id3="baz&amp;&#10;y&amp;&amp;&#9;a &#10;"><xsl:processing-instruction name="pi0">cax&amp;z"</xsl:processing-instruction></SPAN><xsl:comment>x&gt;y</xsl:comment><div a-long-attribute-name2="'b&#9; ba&quot;z&gt;&amp;&gt;&gt; "></div></b></xsl:template>
</xsl:stylesheet>
