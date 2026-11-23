#!/usr/bin/env python3
"""Generates the cases of tests/PhoenixmlDb.Xslt.Tests/SaxonLayout (xslt#499).

Each case is a stylesheet that builds a random tree (elements, namespaces, attributes with characters
that need escaping, text, comments, processing instructions, mixed content, xml:space, CDATA sections,
suppress-indentation, a DOCTYPE) and writes it with indent="yes" and phx:indent-style="saxon-he-10".
The expected result of each case is the output of Saxon-HE 10.9 for the same stylesheet: run this
script with the Saxon-HE 10.9 jar to write it. The cases are fixed by the seed.

    python3 -I scripts/saxon-layout-cases.py <java> <Saxon-HE-10.9.jar> <out-dir> [count]
"""
import random
import subprocess
import sys
from pathlib import Path

PHX = "https://schemas.phoenixml.dev/2026/functions"
NAMESPACES = [("p", "urn:p"), ("q", "urn:example:q"), ("long-prefix", "urn:example:a-much-longer-namespace-uri")]
NAMES = ["a", "b", "item", "section", "x", "data-element", "e"]
TEXT_CHARS = "abc xyz<>&\"'\t\n" + "é"


def esc_text(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def esc_attr(s):
    # In a literal result element the value is an attribute value template: no braces are used.
    return esc_text(s).replace('"', "&quot;").replace("\t", "&#9;").replace("\n", "&#10;")


class Case:
    def __init__(self, rng, index):
        self.rng = rng
        self.index = index
        self.cdata = set()
        self.suppress = set()

    def value(self, low, high):
        n = self.rng.randint(low, high)
        return "".join(self.rng.choice(TEXT_CHARS) for _ in range(n))

    def element(self, depth, in_scope):
        rng = self.rng
        prefix = None
        decls = []
        if rng.random() < 0.25:
            p, uri = rng.choice(NAMESPACES)
            if p not in in_scope:
                decls.append(f' xmlns:{p}="{uri}"')
                in_scope = in_scope | {p}
        if rng.random() < 0.08:
            decls.append(' xmlns="urn:default"' if rng.random() < 0.7 else ' xmlns=""')
        if in_scope and rng.random() < 0.3:
            prefix = rng.choice(sorted(in_scope))
        local = rng.choice(NAMES)
        name = f"{prefix}:{local}" if prefix else local
        attrs = []
        used = set()
        for _ in range(rng.choice([0, 0, 1, 2, 2, 3, 4, 6])):
            aname = rng.choice(["id", "n", "type", "a-long-attribute-name", "v"]) + str(rng.randint(0, 3))
            if in_scope and rng.random() < 0.2:
                aname = f"{rng.choice(sorted(in_scope))}:{aname}"
            if aname in used:
                continue
            used.add(aname)
            attrs.append(f' {aname}="{esc_attr(self.value(0, 30))}"')
        if rng.random() < 0.1:
            attrs.append(' xml:space="preserve"')
        children = []
        if depth < 4:
            for _ in range(rng.choice([0, 0, 1, 2, 3, 4])):
                kind = rng.random()
                if kind < 0.45:
                    children.append(self.element(depth + 1, in_scope))
                elif kind < 0.65:
                    children.append(f"<xsl:text>{esc_text(self.value(0, 12))}</xsl:text>")
                elif kind < 0.8:
                    children.append(f"<xsl:comment>{esc_text(self.value(0, 10).replace('-', ''))}</xsl:comment>")
                elif kind < 0.9:
                    children.append(f'<xsl:processing-instruction name="pi{rng.randint(0, 2)}">'
                                    f"{esc_text(self.value(0, 8).replace('?', ''))}</xsl:processing-instruction>")
                else:
                    children.append("<xsl:text>   </xsl:text>")
        if rng.random() < 0.08:
            self.cdata.add(name)
        if rng.random() < 0.06:
            self.suppress.add(name)
        return f"<{name}{''.join(decls)}{''.join(attrs)}>{''.join(children)}</{name}>"

    def stylesheet(self):
        rng = self.rng
        body = self.element(0, frozenset())
        method = "xhtml" if rng.random() < 0.15 else "xml"
        opts = [f'method="{method}"', 'indent="yes"', 'phx:indent-style="saxon-he-10"']
        if rng.random() < 0.2:
            opts.append('omit-xml-declaration="yes"')
        if rng.random() < 0.15:
            opts.append('doctype-system="case.dtd"')
            if rng.random() < 0.5:
                opts.append('doctype-public="-//PHX//CASE//EN"')
        if self.cdata:
            opts.append(f'cdata-section-elements="{" ".join(sorted(self.cdata))}"')
        if self.suppress:
            opts.append(f'suppress-indentation="{" ".join(sorted(self.suppress))}"')
        ns = "".join(f' xmlns:{p}="{uri}"' for p, uri in NAMESPACES)
        return (f'<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"'
                f' xmlns:phx="{PHX}"{ns} exclude-result-prefixes="phx">\n'
                f"  <xsl:output {' '.join(opts)}/>\n"
                f'  <xsl:template name="xsl:initial-template">{body}</xsl:template>\n'
                f"</xsl:stylesheet>\n")


def main():
    java, jar, out = sys.argv[1], sys.argv[2], Path(sys.argv[3])
    count = int(sys.argv[4]) if len(sys.argv) > 4 else 120
    out.mkdir(parents=True, exist_ok=True)
    rng = random.Random(499)
    for i in range(count):
        xsl = out / f"case{i:03d}.xsl"
        xsl.write_text(Case(rng, i).stylesheet(), encoding="utf-8")
        expected = out / f"case{i:03d}.xml"
        subprocess.run([java, "-cp", jar, "net.sf.saxon.Transform", f"-xsl:{xsl}", "-it", f"-o:{expected}"], check=True)


if __name__ == "__main__":
    main()
