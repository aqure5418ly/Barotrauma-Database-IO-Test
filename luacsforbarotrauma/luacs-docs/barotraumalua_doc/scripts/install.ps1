Import-Module -DisableNameChecking $PSScriptRoot/../../scripts/location.psm1

try {
  Change-Location $PSScriptRoot/..

  $lua_binary = $env:LUA_BINARY
  if ($lua_binary -eq $null) {
    $lua_binary = "lua"
  }

  if ((Get-Command "$lua_binary" -ErrorAction SilentlyContinue) -eq $null) {
    if ($env:LUA_BINARY -eq $null) {
      echo "lua binary not found; please set `$LUA_BINARY manually."
    } else {
      echo "lua binary not found: $lua_binary"
    }
    exit 1
  }

  if ((Get-Command "luarocks" -ErrorAction SilentlyContinue) -eq $null) {
    echo "luarocks not found"
    exit 1
  }

  $lua_version = (Invoke-Expression -Command "& $lua_binary -v 2>&1") -Replace '^Lua (\d+)\.(\d+).*$','$1.$2'
  echo "Detected lua version $lua_version"

  $luarocks_args=@(
    "--tree",
    "$(Get-Location)/lua_modules",
    "--lua-version",
    "$lua_version"
  )

  try {
    Change-Location ./libs/ldoc
    luarocks @luarocks_args make
  } finally {
    Restore-Location
  }
} finally {
  Restore-Location
}
