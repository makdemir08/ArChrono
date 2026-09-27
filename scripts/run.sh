#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
"$ROOT/scripts/dotnet.sh" run --project "$ROOT/src/ArChrono.App/ArChrono.App.csproj" -- "$@"
