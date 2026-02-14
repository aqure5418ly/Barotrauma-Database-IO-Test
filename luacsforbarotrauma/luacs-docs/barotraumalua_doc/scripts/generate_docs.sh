#!/usr/bin/env bash

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
cd "$DIR/LuaDocsGenerator"

if ! command -v "dotnet" &> /dev/null; then
  echo "dotnet not found"
  exit 1
fi

dotnet build -clp:"ErrorsOnly;Summary"
dotnet run --no-build
