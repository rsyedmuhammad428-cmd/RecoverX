#!/usr/bin/env python3
"""Copies the Database project and tests into a temp folder, renaming System.Data.SQLite -> Mono.Data.Sqlite so the real
SQL and repository code can be executed on Linux. Usage: rename_sqlite.py OUT_DIR"""
import glob, os, re, shutil, sys
root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
out = sys.argv[1]
for sub, srcs in (("db", glob.glob(os.path.join(root, "src", "RecoverX.Database", "*.cs"))),
                  ("tests", glob.glob(os.path.join(root, "tests", "CoreTests", "*.cs")))):
    d = os.path.join(out, sub); os.makedirs(d, exist_ok=True)
    for f in srcs:
        s = open(f, encoding="utf-8").read()
        s = s.replace("System.Data.SQLite", "Mono.Data.Sqlite")
        s = re.sub(r"\bSQLite(Connection|Command|Transaction|DataReader)\b", r"Sqlite\1", s)
        s = re.sub(r"var b = new \w*ConnectionStringBuilder\s*\{.*?\};\s*_connectionString = b\.ToString\(\);",
                   '_connectionString = "Data Source=" + databasePath + ";Version=3;foreign keys=true";', s, flags=re.S)
        open(os.path.join(d, os.path.basename(f)), "w", encoding="utf-8").write(s)
