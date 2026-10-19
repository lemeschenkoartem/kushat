#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
if ! command -v dotnet >/dev/null 2>&1 && [[ -x /workspace/.dotnet/dotnet ]]; then
    export PATH="/workspace/.dotnet:$PATH"
fi
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-/workspace/.dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-/workspace/.nuget/packages}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet test tests/Kushat.Core.Tests/Kushat.Core.Tests.csproj -c Release --nologo
dotnet build src/Kushat.App/Kushat.App.csproj -c Release --nologo
