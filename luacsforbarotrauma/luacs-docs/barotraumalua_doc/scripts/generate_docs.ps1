Import-Module -DisableNameChecking $PSScriptRoot/../../scripts/location.psm1

try {
  Change-Location $PSScriptRoot/LuaDocsGenerator

  if ((Get-Command "dotnet" -ErrorAction SilentlyContinue) -eq $null) {
    echo "dotnet not found"
    exit 1
  }

  dotnet build -clp:"ErrorsOnly;Summary"
  dotnet run --no-build
} finally {
  Restore-Location
}
