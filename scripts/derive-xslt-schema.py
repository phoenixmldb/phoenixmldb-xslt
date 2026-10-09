#!/usr/bin/env python3
"""Derives an XSD 1.0 copy of the W3C schema for XSLT 3.0 (an XSD 1.1 schema)."""
import re, sys
src, dst = sys.argv[1], sys.argv[2]
s = open(src, encoding='utf-8').read()
n = {}
NOTICE = """<!--
  MODIFIED COPY. Derived by PhoenixmlDb (scripts/derive-xslt-schema.py, 2026-10-09) from the W3C
  schema for XSLT 3.0, https://www.w3.org/TR/xslt-30/schema-for-xslt30.xsd, so that an XSD 1.0
  processor can load it. Changes:
    - the vc:minVersion="1.1" marker is removed;
    - the 71 assertions (xs:assert, xs:assertion) are removed: the rules they state are NOT checked;
    - xsl:variable, in two substitution groups in the original, is an xsl:instruction, and is
      named where declarations are allowed;
    - an inline xs:schema in xsl:import-schema is checked laxly; the schema for schemas is not imported;
    - two union members that restrict a union with a pattern are written out as their members;
    - names on xsl:accept and xsl:expose is xs:token (the original refuses name tests and name#arity);
    - the serialization methods json and adaptive are added (the original omits them).
  The original is copyright W3C and is used under the W3C Software License,
  http://www.w3.org/Consortium/Legal/copyright-software-19980720
-->
"""
def sub(key, pat, rep, expect, flags=re.S):
    global s
    s, c = re.subn(pat, rep, s, flags=flags)
    n[key] = c
    if c != expect:
        sys.exit(f"{key}: expected {expect} replacement(s), made {c}")

# 1. The version marker.
sub('minVersion', r'\s+vc:minVersion="1\.1"', '', 1)
sub('vc-ns', r'\s+xmlns:vc="http://www\.w3\.org/2007/XMLSchema-versioning"', '', 1)
# 2. Assertions (XSD 1.1 only): on complex types and as facets.
s, a1 = re.subn(r'[ \t]*<xs:assert(?:ion)?\b[^>]*/>\n?', '', s, flags=re.S)
s, a2 = re.subn(r'[ \t]*<xs:assert(?:ion)?\b[^>/]*(?:/(?!>)[^>/]*)*>.*?</xs:assert(?:ion)?>\n?', '', s, flags=re.S)
n['assertions'] = a1 + a2
if a1 + a2 != 71 or '<xs:assert' in s:
    sys.exit(f"assertions: expected 71, removed {a1 + a2}")
# 3. xsl:variable is in two substitution groups. Keep it an instruction, and name it where
#    declarations are allowed.
sub('variable-heads', r'(<xs:element name="variable" substitutionGroup=)"xsl:declaration xsl:instruction"', r'\1"xsl:instruction"', 1)
sub('declaration-refs', r'([ \t]*)<xs:element ref="xsl:declaration"/>\n', r'\1<xs:element ref="xsl:declaration"/>\n\1<xs:element ref="xsl:variable"/>\n', 2)
# 4. The schema for schemas is not imported: an inline xs:schema is checked laxly.
sub('xs-schema-ref', r'<xs:element ref="xs:schema" minOccurs="0" maxOccurs="1"/>',
    '<xs:any namespace="http://www.w3.org/2001/XMLSchema" processContents="lax" minOccurs="0" maxOccurs="1"/>', 1)
sub('xsd-import', r'[ \t]*<xs:import namespace="http://www\.w3\.org/2001/XMLSchema"\s+schemaLocation="[^"]*"/>\n', '', 1)
# 5. A union member that restricts a union with a facet. Written out as the members the
#    facet leaves: the value space is the same.
sub('method-member', r'<xs:restriction base="xsl:EQName">\s*<xs:pattern value="\\c\*:\\c\*"/>\s*</xs:restriction>',
    '<xs:restriction base="xs:QName">\n          <xs:pattern value="\\\\c*:\\\\c*"/>\n        </xs:restriction>', 1)
sub('eqname-in-namespace', r'<xs:restriction base="xsl:EQName">\s*<xs:pattern value="Q\\\{\.\+\\\}\.\+\|\\i\\c\*:\.\+"/>\s*</xs:restriction>',
    '<xs:union>\n      <xs:simpleType>\n        <xs:restriction base="xs:QName">\n          <xs:pattern value="\\\\i\\\\c*:.+"/>\n        </xs:restriction>\n      </xs:simpleType>\n'
    '      <xs:simpleType>\n        <xs:restriction base="xs:token">\n          <xs:pattern value="Q\\\\{[^{}]+\\\\}[\\\\i-[:]][\\\\c-[:]]*"/>\n        </xs:restriction>\n      </xs:simpleType>\n    </xs:union>', 1)
# 6. Two corrections to the W3C schema itself, which refuses valid XSLT 3.0:
#    names on xsl:accept and xsl:expose is a list of name tests and name#arity tokens, not of
#    EQNames; and the serialization methods json and adaptive are missing.
sub('names-tokens', r'<xs:attribute name="names" type="xsl:EQNames"/>', '<xs:attribute name="names" type="xs:token"/>', 2)
sub('method-json', r'(<xs:enumeration value="text"/>\n)(\s*)(</xs:restriction>\s*</xs:simpleType>\s*<xs:simpleType>\s*<xs:restriction base="xs:QName">)',
    r'\1\2  <xs:enumeration value="json"/>\n\2  <xs:enumeration value="adaptive"/>\n\2\3', 1)
# 7. Say what this file is (the W3C Software License asks for notice of changes).
sub('notice', r'(<xs:schema xmlns:xs=)', lambda m: NOTICE + m.group(1), 1)
sub('doc-line', r'This is an XSD 1\.1 schema for XSLT 3\.0 stylesheets\.',
    'This is an XSD 1.0 copy of the XSD 1.1 schema for XSLT 3.0 stylesheets.', 1)
open(dst, 'w', encoding='utf-8').write(s)
print(n)
