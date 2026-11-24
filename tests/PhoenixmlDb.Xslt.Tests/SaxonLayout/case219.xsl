<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" html-version="4.0"/>
  <xsl:template name="xsl:initial-template"><html xmlns="http://www.w3.org/1999/xhtml"><TITLE xmlns="http://www.w3.org/1999/xhtml" a-long-attribute-name2="y&#9;bz&lt;yc&#10;&#9;&gt;aé&#10;&quot;c&quot;" a-long-attribute-name3=" &gt;é" n1="&gt; &#10;&#9; xaéa&#9;" type1="ba&#10;xy&#9;é&#10;z&gt;zé&gt;x&#9;&#10;x&#9;b" type2="'bc&quot;&#9;a &gt;&#9;&lt;cyb"></TITLE></html></xsl:template>
</xsl:stylesheet>
