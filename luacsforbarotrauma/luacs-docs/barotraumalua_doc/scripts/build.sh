#!/usr/bin/env bash

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
cd "$DIR/.."

ldoc_path=./lua_modules/bin/ldoc

if [[ ! -x "$ldoc_path" ]]; then
  echo "ldoc not found; please run scripts/install.sh"
  exit 1
fi

rm -rf ./build
mkdir ./build

cp -r ./js/. ./build
cp -r ./css/. ./build

"$ldoc_path" .
