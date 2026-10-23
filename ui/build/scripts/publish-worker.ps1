#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes the chat host (Scout.Host.Console) and the control-plane host
    (Scout.Host.ControlPlane) as self-contained single-file binaries for the
    requested RID and places both under ui/build/worker/<RID>/.

.PARAMETER RID
    Target runtime identifier. Defaults to win-x64 when running on Windows.
    Supported values: linux-x64, osx-arm64, osx-x64, win-x64.
#>
param(
    [string]$RID = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$SupportedRids = @("linux-x64", "osx-arm64", "osx-x64", "win-x64")

function Detect-Rid {
    $os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture

    if ($IsWindows -or $os -like "*Windows*") {
        return "win-x64"
    } elseif ($IsMacOS -or $os -like "*Darwin*") {
        if ($arch -eq "Arm64") { return "osx-arm64" }
        return "osx-x64"
    } else {
        return "linux-x64"
    }
}

if ([string]::IsNullOrWhiteSpace($RID)) {
    $RID = Detect-Rid
}

if ($SupportedRids -notcontains $RID) {
    Write-Error "Unsupported RID '$RID'. Supported: $($SupportedRids -join ', ')"
    exit 1
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = (Resolve-Path (Join-Path $ScriptDir "../../..")).Path
$UiRoot    = Join-Path $RepoRoot "ui"
$Hosts     = @("Scout.Host.Console", "Scout.Host.ControlPlane")
$OutputDir = Join-Path $UiRoot "build\worker\$RID"

foreach ($HostName in $Hosts) {
    $Csproj = Join-Path $RepoRoot "src\$HostName\$HostName.csproj"
    Write-Host "publishing $HostName for $RID -> $OutputDir"

    & dotnet publish $Csproj `
        --configuration Release `
        --runtime $RID `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        --output $OutputDir

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    if ($RID -eq "win-x64") {
        $BinaryName = "$HostName.exe"
    } else {
        $BinaryName = $HostName
    }

    $Binary = Join-Path $OutputDir $BinaryName

    if (-not (Test-Path $Binary)) {
        Write-Error "Expected binary not found at $Binary"
        exit 1
    }

    $Size = (Get-Item $Binary).Length
    if ($Size -eq 0) {
        Write-Error "Binary at $Binary has zero size"
        exit 1
    }

    Write-Host "ok: $Binary ($Size bytes)"
}
