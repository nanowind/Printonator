#!/bin/bash
# Build Printonator setup wizard (Inno Setup)
set -e
cd "$(dirname "$0")/.."

echo "== 1/4 Publish app (Release) =="
dotnet publish src/Printonator.UI -c Release -r win-x64 --self-contained false -o setup/app 2>&1 | tail -2

echo "== 2/4 Build setup EXE =="
ISCC="/c/Users/scraw/AppData/Local/Programs/Inno Setup 6/ISCC.exe"
cd setup
rm -f printonator-setup.exe
"$ISCC" printonator.iss 2>&1 | tail -6

echo "== 3/4 Done =="
cd ..
ls -la setup/printonator-setup.exe
echo "Setup wizard: setup/printonator-setup.exe"