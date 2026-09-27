#!/usr/bin/env bash
# Kullanıcı dizinindeki .NET 10 SDK'sını (~/.dotnet) tercih eden dotnet sarmalayıcısı.
set -euo pipefail
if [[ -x "$HOME/.dotnet/dotnet" ]] && "$HOME/.dotnet/dotnet" --list-sdks | grep -q '^10\.'; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$DOTNET_ROOT:$PATH"
fi
exec dotnet "$@"
