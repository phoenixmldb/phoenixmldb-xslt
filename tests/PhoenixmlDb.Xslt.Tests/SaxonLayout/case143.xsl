<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" doctype-system="case.dtd" doctype-public="-//PHX//CASE//EN"/>
  <xsl:template name="xsl:initial-template"><TEXTAREA xmlns:q="urn:example:q" n2="&amp;&quot;&amp;c&gt; &amp;b&#9;&#10;&#9;&lt; 'a&#9; x&#10;" id1="&lt;&gt;&gt;b&#9;é&lt;&#10;&amp;yzy&amp;"></TEXTAREA></xsl:template>
</xsl:stylesheet>
