[CmdletBinding()]
param(
    [ValidateSet("binary", "source")]
    [string]$Channel = "binary"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$modRoot = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$workshopRoot = [System.IO.Path]::GetFullPath((Join-Path $modRoot ".."))
$syncMapPath = Join-Path $workshopRoot "sync-map.json"
$filelistPath = Join-Path $modRoot "filelist.xml"
$binaryTemplatePath = Join-Path $modRoot "filelist.binary.xml"
$sourceTemplatePath = Join-Path $modRoot "filelist.source.xml"

if (-not (Test-Path -LiteralPath $syncMapPath -PathType Leaf)) {
    throw "sync-map.json not found: $syncMapPath"
}

if (-not (Test-Path -LiteralPath $binaryTemplatePath -PathType Leaf)) {
    throw "binary template not found: $binaryTemplatePath"
}

if (-not (Test-Path -LiteralPath $sourceTemplatePath -PathType Leaf)) {
    throw "source template not found: $sourceTemplatePath"
}

$config = Get-Content -LiteralPath $syncMapPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $config.mappings) {
    throw "No mappings found in sync-map.json"
}

$mapping = $config.mappings | Where-Object { "$($_.project)" -eq "Database IO Test" } | Select-Object -First 1
if ($null -eq $mapping) {
    throw "Mapping 'Database IO Test' not found in sync-map.json"
}

if ($Channel -eq "binary") {
    $mapping.buildScript = "build-assembly.ps1"
    $mapping.buildArgs = @("-Configuration", "Release", "-Target", "all")
    $mapping.includePaths = @(
        "filelist.xml",
        "XML",
        "Textures",
        "CSharp/RunConfig.xml",
        "bin",
        "debug.enabled"
    )
    $mapping.prunePaths = @(
        "CSharp/Shared",
        "CSharp/DatabaseIOTest.buildcheck.csproj"
    )

    Copy-Item -LiteralPath $binaryTemplatePath -Destination $filelistPath -Force
}
else {
    $mapping.buildScript = ""
    $mapping.buildArgs = @()
    $mapping.includePaths = @(
        "filelist.xml",
        "XML",
        "Textures",
        "CSharp",
        "debug.enabled"
    )
    $mapping.prunePaths = @(
        "bin"
    )

    Copy-Item -LiteralPath $sourceTemplatePath -Destination $filelistPath -Force
}

$json = $config | ConvertTo-Json -Depth 20
Set-Content -LiteralPath $syncMapPath -Value $json -Encoding UTF8

Write-Host ("Switched Database IO Test channel to '{0}'." -f $Channel)
Write-Host ("Updated: {0}" -f $syncMapPath)
Write-Host ("Updated: {0}" -f $filelistPath)
Write-Host "Tip: run install-sync-hooks.ps1 once if hook behavior was customized."
