#!/usr/bin/env bash

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
cd "$DIR/.."

lua_binary="${LUA_BINARY:-lua}"

if ! command -v "$lua_binary" &> /dev/null; then
  if [[ -z "${LUA_BINARY+x}" ]]; then
    echo "lua binary not found; please set \$LUA_BINARY manually."
  else
    echo "lua binary not found: $lua_binary"
  fi
  exit 1
fi

if ! command -v "$lua_binary" &> /dev/null; then
  echo "luarocks not found"
  exit 1
fi

lua_version="$("$lua_binary" -v | grep -Po '^Lua \K(\d+)\.(\d+)')"
echo "Detected lua version $lua_version"

# Install dependencies (npm style)
# NOTE: you need to have lua header files installed.
# On debian-based distros: apt install libluaX.X-dev

luarocks_args=(
  "--tree"
  "$PWD/lua_modules"
  "--lua-version"
  "$lua_version"
)

(
  cd libs/ldoc
  luarocks ${luarocks_args[@]} make
)
