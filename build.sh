#!/usr/bin/env bash
# Build helper for VideoAnalyzer.
#
# Why the env block: this project is built from an agent shell whose environment
# is missing the standard Windows variables. NuGet locates its machine-wide
# config through PROGRAMFILES(X86)/PROGRAMFILES and throws
# "Value cannot be null. (Parameter 'path1')" when they are absent, which makes
# every restore fail. Supplying them here keeps `dotnet build` reproducible.
#
# Usage:  ./build.sh [extra dotnet args]
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$HERE/src/VideoAnalyzer"

exec env \
  'PROGRAMFILES=C:\Program Files' \
  'PROGRAMFILES(X86)=C:\Program Files (x86)' \
  'PROGRAMDATA=C:\ProgramData' \
  'ALLUSERSPROFILE=C:\ProgramData' \
  'APPDATA=C:\Users\Administrator\AppData\Roaming' \
  'LOCALAPPDATA=C:\Users\Administrator\AppData\Local' \
  'USERPROFILE=C:\Users\Administrator' \
  'HOMEDRIVE=C:' \
  'HOMEPATH=\Users\Administrator' \
  dotnet build -nologo "$@"
