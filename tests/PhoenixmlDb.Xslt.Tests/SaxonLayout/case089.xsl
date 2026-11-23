<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xml" indent="yes" phx:indent-style="saxon-he-10"/>
  <xsl:template name="xsl:initial-template"><long-prefix:x xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" long-prefix:n2="b é&lt; a&#9;z&amp;&gt; b&#10;&quot;yy&#10;éz&#10;za b'&amp;&gt;" a-long-attribute-name1="&amp;é&gt;&gt;y&quot;'&amp;yx&#10;xy&#9;abaazy&gt;é&#9;&#9;&quot;cbx" xml:space="preserve"></long-prefix:x></xsl:template>
</xsl:stylesheet>
