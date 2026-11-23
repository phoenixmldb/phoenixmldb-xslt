<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:phx="https://schemas.phoenixml.dev/2026/functions" xmlns:p="urn:p" xmlns:q="urn:example:q" xmlns:long-prefix="urn:example:a-much-longer-namespace-uri" exclude-result-prefixes="phx">
  <xsl:output method="xhtml" indent="yes" phx:indent-style="saxon-he-10" cdata-section-elements="a" suppress-indentation="a"/>
  <xsl:template name="xsl:initial-template"><a xmlns="http://www.w3.org/1999/xhtml" n1="&gt;' ab'" type2="é&#10;é&#9;" a-long-attribute-name2="b&lt;&gt;ya''zé"><xsl:text>é'</xsl:text><script v0="&#10;''b&amp;c&#9;y" n0="z&#10;xzbz&#9;z"><xsl:text>   </xsl:text></script><li xmlns="urn:default"></li><xsl:text>&gt;c&lt;	yz&gt;&lt;</xsl:text></a></xsl:template>
</xsl:stylesheet>
