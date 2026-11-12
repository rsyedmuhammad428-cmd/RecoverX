#!/usr/bin/env python3
"""Localization lint for RecoverX.
 1. Every {loc:Loc Key} in XAML and every Get("Key") / Format("Key") in C# must exist in Resources/Strings.resx.
 2. Dynamic keys (Cat_<id>, ScanType_<type>, Status_<status>) must exist for every value the app can produce.
 3. Every other Strings.<lang>.resx must define exactly the same keys as Strings.resx (so adding Urdu cannot leave gaps).
Run:  python scripts/check-localization.py     (exit code 1 on any problem)"""
import glob, os, re, sys
from xml.dom import minidom

root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
res_dir = os.path.join(root, "src", "RecoverX.Desktop", "Resources")

def keys_of(path):
    doc = minidom.parse(path)
    return {d.getAttribute("name"): d.getElementsByTagName("value")[0].firstChild.data if d.getElementsByTagName("value")[0].firstChild else ""
            for d in doc.getElementsByTagName("data")}

base = keys_of(os.path.join(res_dir, "Strings.resx"))
problems = []

used = set()
for f in glob.glob(os.path.join(root, "src", "**", "*.xaml"), recursive=True):
    for m in re.finditer(r"\{loc:Loc\s+([A-Za-z0-9_]+)\}", open(f, encoding="utf-8").read()):
        used.add((m.group(1), os.path.relpath(f, root)))
# C#: any string literal shaped like a resource key (Area_Name). Literals ending in "_" are prefixes for dynamic keys.
for f in glob.glob(os.path.join(root, "src", "RecoverX.Desktop", "**", "*.cs"), recursive=True) + glob.glob(os.path.join(root, "src", "RecoverX.Core", "**", "*.cs"), recursive=True):
    for m in re.finditer(r'"([A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+)"', open(f, encoding="utf-8").read()):
        used.add((m.group(1), os.path.relpath(f, root)))
for k, where in sorted(used):
    if k not in base: problems.append("missing key '%s' (used in %s)" % (k, where))

dynamic = ["Cat_" + c for c in ("Photos", "Videos", "Documents", "Audio", "Archives", "Other")]
dynamic += ["ScanType_" + t for t in ("Quick", "Deep")]
dynamic += ["Status_" + s for s in ("Completed", "Cancelled", "Failed", "Interrupted", "Running", "Verified", "Unverified", "Suspect")]
# keys returned by DriveClassifier
cls = open(os.path.join(root, "src", "RecoverX.Core", "Services", "DriveClassifier.cs"), encoding="utf-8").read()
dynamic += re.findall(r'"((?:Media|Bus|Loc|Warn|Source)_[A-Za-z0-9]+)"', cls)
for k in sorted(set(dynamic)):
    if k not in base: problems.append("missing dynamic key '%s'" % k)

for f in sorted(glob.glob(os.path.join(res_dir, "Strings.*.resx"))):
    other = keys_of(f)
    for k in sorted(set(base) - set(other)): problems.append("%s lacks key '%s'" % (os.path.basename(f), k))
    for k in sorted(set(other) - set(base)): problems.append("%s has unknown key '%s'" % (os.path.basename(f), k))
    # format placeholders must agree, or string.Format would throw or drop values
    for k in set(base) & set(other):
        if sorted(re.findall(r"\{\d+\}", base[k])) != sorted(re.findall(r"\{\d+\}", other[k])):
            problems.append("%s: placeholders differ for '%s'" % (os.path.basename(f), k))

unused = sorted(set(base) - {k for k, _ in used} - set(dynamic))
print("keys defined: %d | referenced: %d | languages checked: %d" % (len(base), len({k for k, _ in used}), 1 + len(glob.glob(os.path.join(res_dir, 'Strings.*.resx')))))
if unused: print("note: unused keys:", ", ".join(unused))
if problems:
    print("\n".join("PROBLEM: " + p for p in problems)); sys.exit(1)
print("localization check passed")
