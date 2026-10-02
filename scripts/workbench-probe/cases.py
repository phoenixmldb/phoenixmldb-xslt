#!/usr/bin/env python3
"""Lists the workbench examples as probe cases, from the workbench itself so new examples are
picked up without editing the gate. XSLT: every <option> in Pages/Home.razor with a code and an
input URL. Schematron: each examples/xslt/schematron/*.sch with a same-stem .xml, else the only
.xml in that folder that no other .sch claims. DocBook: samples/custom.xsl over each sample .xml.
Usage: cases.py <workbench project dir>  ->  kind<TAB>id<TAB>code<TAB>input lines on stdout."""
import os, re, sys
wb = sys.argv[1]; ex = os.path.join(wb, 'wwwroot', 'examples')
rel = lambda p: os.path.relpath(p, ex).replace(os.sep, '/')
home = open(os.path.join(wb, 'Pages', 'Home.razor'), encoding='utf-8').read()
for opt in re.findall(r'<option\b[^>]*>', home):
    code = re.search(r'data-code-url="([^"]+)"', opt); inp = re.search(r'data-input-url="([^"]+)"', opt)
    if code and inp and code.group(1).endswith('.xsl'):
        c = code.group(1).removeprefix('examples/'); i = inp.group(1).removeprefix('examples/')
        print(f"xsl\t{c.removeprefix('xslt/').removesuffix('.xsl')}\t{c}\t{i}")
sd = os.path.join(ex, 'xslt', 'schematron')
if os.path.isdir(sd):
    schs = sorted(f for f in os.listdir(sd) if f.endswith('.sch')); xmls = sorted(f for f in os.listdir(sd) if f.endswith('.xml'))
    claimed = {s[:-4] + '.xml' for s in schs if s[:-4] + '.xml' in xmls}
    spare = [x for x in xmls if x not in claimed]
    for s in schs:
        x = s[:-4] + '.xml' if s[:-4] + '.xml' in xmls else (spare[0] if len(spare) == 1 else None)
        if x: print(f"sch\tschematron/{s[:-4]}\t{rel(os.path.join(sd, s))}\t{rel(os.path.join(sd, x))}")
dd = os.path.join(ex, 'xslt', 'docbook', 'samples')
if os.path.isfile(os.path.join(dd, 'custom.xsl')):
    for x in sorted(f for f in os.listdir(dd) if f.endswith('.xml')):
        print(f"docbook\tdocbook/{x[:-4]}\t{rel(os.path.join(dd, 'custom.xsl'))}\t{rel(os.path.join(dd, x))}")
