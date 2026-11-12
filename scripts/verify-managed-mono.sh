#!/bin/sh
# Runs the managed (C#) test suite on Linux under Mono, against a real SQLite engine. Needs: mono-mcs, libmono-sqlite4.0-cil.
# This does NOT test WPF or the Windows-only code; it exercises Core logic, the Database project's real SQL, and the recovery
# service with real file I/O.
set -e
cd "$(dirname "$0")/.."
W=$(mktemp -d)
python3 tests/mono/rename_sqlite.py "$W"
mcs -out:"$W/CoreTests.exe" -langversion:7 -r:System.Core.dll -r:System.Data.dll -r:/usr/lib/mono/4.5/Mono.Data.Sqlite.dll \
    $(find src/RecoverX.Core -name "*.cs") "$W"/db/*.cs "$W"/tests/*.cs tests/mono/MsTestShim.cs
cp /usr/lib/mono/4.5/Mono.Data.Sqlite.dll "$W/"
(cd "$W" && mono CoreTests.exe)
rm -rf "$W"
