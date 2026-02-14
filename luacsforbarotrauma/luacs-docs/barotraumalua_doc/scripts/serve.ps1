Import-Module -DisableNameChecking $PSScriptRoot/../../scripts/location.psm1

try {
  Change-Location $PSScriptRoot/..

  if ((Get-Command "python3" -ErrorAction SilentlyContinue) -eq $null) {
    echo "python3 not found"
    exit 1
  }

  python3 ../scripts/http_server.py ./build --port 8000
} finally {
  Restore-Location
}
