[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("all", "client", "server", "windows", "linux", "osx")]
    [string]$Target = "all"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$modRoot = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$solutionRoot = Join-Path $modRoot ".AssemblyCSharpSource\DatabaseIOTest"
$refsRoot = Join-Path $modRoot ".AssemblyCSharpSource\.Tools\Refs"

if (-not (Test-Path -LiteralPath $solutionRoot -PathType Container)) {
    throw "Assembly build root not found: $solutionRoot"
}

if (-not (Test-Path -LiteralPath $refsRoot -PathType Container)) {
    throw "Refs directory not found: $refsRoot. Extract luacsforbarotrauma_refs.zip first."
}

$commonRefs = @(
    "0Harmony.dll",
    "Farseer.NetStandard.dll",
    "Lidgren.NetStandard.dll",
    "Mono.Cecil.dll",
    "MonoMod.Utils.dll",
    "MoonSharp.Interpreter.dll",
    "XNATypes.dll",
    "MonoGame.Framework.Windows.NetStandard.dll",
    "MonoGame.Framework.Linux.NetStandard.dll",
    "MonoGame.Framework.MacOS.NetStandard.dll"
)

$missingCommon = @($commonRefs | Where-Object { -not (Test-Path -LiteralPath (Join-Path $refsRoot $_) -PathType Leaf) })
if ($missingCommon.Count -gt 0) {
    throw ("Missing common refs:`n- " + ($missingCommon -join "`n- "))
}

$osRefs = @(
    "Windows\Barotrauma.dll",
    "Windows\DedicatedServer.dll",
    "Windows\BarotraumaCore.dll",
    "Linux\Barotrauma.dll",
    "Linux\DedicatedServer.dll",
    "Linux\BarotraumaCore.dll",
    "OSX\Barotrauma.dll",
    "OSX\DedicatedServer.dll",
    "OSX\BarotraumaCore.dll"
)

$missingOs = @($osRefs | Where-Object { -not (Test-Path -LiteralPath (Join-Path $refsRoot $_) -PathType Leaf) })
if ($missingOs.Count -gt 0) {
    throw ("Missing platform refs:`n- " + ($missingOs -join "`n- "))
}

$projectMap = [ordered]@{
    "windows-client" = "ClientProject\WindowsClient.csproj"
    "linux-client"   = "ClientProject\LinuxClient.csproj"
    "osx-client"     = "ClientProject\OSXClient.csproj"
    "windows-server" = "ServerProject\WindowsServer.csproj"
    "linux-server"   = "ServerProject\LinuxServer.csproj"
    "osx-server"     = "ServerProject\OSXServer.csproj"
}

$selected = switch ($Target) {
    "all"     { @($projectMap.Keys) }
    "client"  { @("windows-client", "linux-client", "osx-client") }
    "server"  { @("windows-server", "linux-server", "osx-server") }
    "windows" { @("windows-client", "windows-server") }
    "linux"   { @("linux-client", "linux-server") }
    "osx"     { @("osx-client", "osx-server") }
    default   { throw "Unsupported target: $Target" }
}

Write-Host "Building DatabaseIOTest assembly projects..."
Write-Host "Configuration: $Configuration"
Write-Host "Target: $Target"

foreach ($key in $selected) {
    $projRel = $projectMap[$key]
    $projPath = Join-Path $solutionRoot $projRel
    if (-not (Test-Path -LiteralPath $projPath -PathType Leaf)) {
        throw "Project file not found: $projPath"
    }

    Write-Host "-> $key"
    $intermediateRoot = Join-Path $env:TEMP ("dbiotest-assembly-obj\" + $key + "\")
    if (-not (Test-Path -LiteralPath $intermediateRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $intermediateRoot -Force | Out-Null
    }

    $dotnetArgs = @(
        "build",
        $projPath,
        "-c", $Configuration,
        "-nologo",
        "-p:BaseIntermediateOutputPath=$intermediateRoot",
        "-p:MSBuildProjectExtensionsPath=$intermediateRoot",
        "-p:IntermediateOutputPath=$intermediateRoot"
    )

    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed: $projRel"
    }
}

Write-Host "Assembly build completed."
