#!/usr/bin/env python3
"""The W3C cases that this processor reads into memory although the rules of XSLT 3.0 §19.8 accept
them: under StrictStreamability they give phx:XTSE9430 (the principal input) or phx:XTDE9430
(xsl:source-document), BUGS.md #164 and xslt#143.

Make the list again from a conformance run in which the harness raises these errors:

    CONFORMANCE_STRICT_READ_WHOLE=error CONFORMANCE_OUT=/tmp/strict-read-whole ./scripts/conformance.sh
    python3 -I scripts/strict-streaming-read-whole.py /tmp/strict-read-whole > scripts/strict-streaming-read-whole.tsv

The per-set baseline gate of that run fails, as it must: the cases are counted as failures there.
The counts go to stderr (by code, by set, by instruction), with the number of FAILED entries read,
so that a run with no logs does not read as "no cases".

Columns: set, case, code, construct (the expression or the instruction the error names).
"""
import glob
import os
import re
import sys
from collections import Counter

ERROR = re.compile(
    r'(phx:XT[SD]E9430): The (?:input|document of xsl:source-document) cannot be streamed: '
    r'this processor cannot evaluate (.*?) \((?:the template [^)]*\)?|the body of xsl:source-document)\) while streaming')


def main(logdir):
    rows = []
    failed_entries = 0
    logs = sorted(glob.glob(os.path.join(logdir, '*.log')))
    for path in logs:
        current_set = '?'
        lines = open(path, encoding='utf-8', errors='replace').read().splitlines()
        for i, line in enumerate(lines):
            m = re.search(r'Running \d+ tests from tests/(\S+)/_[^/]+\.xml', line)
            if m:
                current_set = m.group(1)
                continue
            m = re.search(r'FAILED: (\S+)', line)
            if not m:
                continue
            failed_entries += 1
            for follow in lines[i + 1:i + 6]:
                if 'FAILED: ' in follow or 'Running ' in follow:
                    break
                e = ERROR.search(follow)
                if e:
                    rows.append((current_set, m.group(1), e.group(1), e.group(2)))
                    break
    rows.sort()
    for row in rows:
        print('\t'.join(row))
    print(f'logs={len(logs)} failed-entries={failed_entries} cases={len(rows)}', file=sys.stderr)
    for title, key in (('code', 2), ('set', 0), ('construct', 3)):
        for value, count in Counter(r[key] for r in rows).most_common():
            print(f'  {title} {value}: {count}', file=sys.stderr)


if __name__ == '__main__':
    if len(sys.argv) != 2:
        sys.exit('usage: strict-streaming-read-whole.py CONFORMANCE_OUT_DIR')
    main(sys.argv[1])
