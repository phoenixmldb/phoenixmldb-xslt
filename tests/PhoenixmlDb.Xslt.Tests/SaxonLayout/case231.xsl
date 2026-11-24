<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" omit-xml-declaration="yes" html-version="5"/>
  <xsl:template name="xsl:initial-template"><span xmlns:p="urn:p" id2="bc&#10;'" id1="&lt;&gt;&#9; b&quot;z''" v0="&amp;b&lt;z&quot;&amp;y&#9;&lt;&amp; b &quot;&lt;" p:a-long-attribute-name1="'&lt;z&amp;'" a-long-attribute-name1="&#9;&amp;&lt;&quot;bab&#9;&#10;&#9;&amp;&lt;c &#10;&#9;&amp;b&quot;zc&#10;"></span></xsl:template>
</xsl:stylesheet>
