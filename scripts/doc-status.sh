#!/usr/bin/env bash
# doc-status.sh — stamp a STATUS banner onto a doc, idempotently.
#
# Why this exists: 156 of the docs under docs/ are cited from `.cs` source comments, so they cannot be
# moved into an archive directory without breaking a pointer a developer follows from code. The banner is
# the alternative — the path keeps resolving, and one line at the top tells the reader whether to trust
# what follows. See docs/DOCS-HOUSEKEEPING-PLAN.md §4.
#
# Usage:
#   scripts/doc-status.sh <file.md> <STATUS> <note...>
#
# STATUS is free text but the pass uses a fixed vocabulary: CURRENT | ARCHIVED | SUPERSEDED |
# HISTORICAL TRAIL | NEVER IMPLEMENTED.
#
# The banner is inserted as a blockquote immediately after the first `# Title` line. Re-running with a
# different status REPLACES the existing banner rather than stacking a second one, so this is safe to
# run repeatedly (a stacked banner history is exactly the noise this pass is removing).
set -euo pipefail

if [[ $# -lt 3 ]]; then
  echo "usage: $0 <file.md> <STATUS> <note...>" >&2
  exit 2
fi

file="$1"; status="$2"; shift 2; note="$*"

[[ -f "$file" ]] || { echo "no such file: $file" >&2; exit 1; }

marker="> **STATUS:"

python3 - "$file" "$status" "$note" <<'PY'
import sys

path, status, note = sys.argv[1], sys.argv[2], sys.argv[3]
with open(path, encoding='utf-8') as fh:
    lines = fh.readlines()

banner = f"> **STATUS: {status}** — {note}\n"

# Find the title line (first line starting with a single '# ').
title = next((i for i, l in enumerate(lines) if l.startswith('# ')), None)
if title is None:
    # No title: put the banner at the very top rather than guessing where a heading belongs.
    insert_at = 0
else:
    insert_at = title + 1

# Drop an existing banner (and the blank line that follows it) so statuses replace rather than stack.
scan = insert_at
while scan < len(lines) and lines[scan].strip() == '':
    scan += 1
if scan < len(lines) and lines[scan].startswith('> **STATUS:'):
    end = scan
    while end < len(lines) and lines[end].startswith('>'):
        end += 1
    while end < len(lines) and lines[end].strip() == '':
        end += 1
    del lines[scan:end]

lines[insert_at:insert_at] = ['\n', banner, '\n']

with open(path, 'w', encoding='utf-8') as fh:
    fh.writelines(lines)

print(f"stamped {status}: {path}")
PY
