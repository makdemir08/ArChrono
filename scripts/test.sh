#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
"$ROOT/scripts/dotnet.sh" test "$ROOT/tests/ArChrono.Tests/ArChrono.Tests.csproj" "$@"
