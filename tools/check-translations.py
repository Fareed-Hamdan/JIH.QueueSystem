#!/usr/bin/env python3
"""Checks that every text key used in the UI and every business-rule error has an Arabic/English entry in Loc.cs.
Usage: python3 tools/check-translations.py   (exit code 1 if something is missing)"""
import glob, os, re, sys
root = os.path.join(os.path.dirname(__file__), "..", "src")
loc = open(os.path.join(root, "JIH.Queue.Web/Services/Loc.cs"), encoding="utf-8").read()
keys = set(re.findall(r'\["([^"]+)"\]\s*=\s*\(', loc))
used = set()
for f in glob.glob(os.path.join(root, "JIH.Queue.Web/Components/**/*.*"), recursive=True):
    s = open(f, encoding="utf-8").read()
    for rx in (r'L\["([a-zA-Z0-9_.]+)"\]', r'L\.Format\("([a-zA-Z0-9_.]+)"', r'Loc\.Get\("([a-zA-Z0-9_.]+)"', r'Bi\("([a-zA-Z0-9_.]+)"\)'):
        used |= set(re.findall(rx, s))
for f in glob.glob(os.path.join(root, "JIH.Queue.Core/**/*.cs"), recursive=True):
    used |= set(re.findall(r'"(err\.[a-zA-Z]+)"', open(f, encoding="utf-8").read()))
missing = sorted(used - keys)
print("All texts translated." if not missing else "Missing keys:\n  " + "\n  ".join(missing))
sys.exit(1 if missing else 0)
