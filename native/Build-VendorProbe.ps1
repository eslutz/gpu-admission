param(
    [Parameter(Mandatory)][string]$AdlxSdk,
    [Parameter(Mandatory)][string]$IgclSdk,
    [Parameter(Mandatory)][string]$Destination,
    [string]$Zig
)
$ErrorActionPreference='Stop'

$amd=(Resolve-Path -LiteralPath $AdlxSdk).Path
$intel=(Resolve-Path -LiteralPath $IgclSdk).Path
$destinationPath=[IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination))
[void][IO.Directory]::CreateDirectory($destinationPath)
$output=Join-Path $destinationPath 'GpuAdmissionVendorProbe.exe'
$src=Join-Path $PSScriptRoot 'VendorProbe.cpp'
$amdInclude=Join-Path $amd 'SDK/Include'
$intelInclude=Join-Path $intel 'include'
$intelWrapper=Join-Path $intel 'Source/cApiWrapper.cpp'

if($Zig) {
    $compiler=(Resolve-Path -LiteralPath $Zig).Path
    & $compiler c++ -target x86_64-windows-gnu -std=c++17 -O2 -Wno-unknown-attributes -I $amdInclude -I $intelInclude $src $intelWrapper -o $output
} else {
    # Run in an x64 Native Tools Command Prompt, or load DevShell before invoking the script.
    Push-Location -LiteralPath $destinationPath
    try {
        & cl.exe /nologo /std:c++17 /O2 /MT /EHsc /DNDEBUG /D_WIN32_WINNT=0x0A00 "/I$amdInclude" "/I$intelInclude" $src $intelWrapper "/Fe$output"
    } finally { Pop-Location }
}
if($LASTEXITCODE){exit $LASTEXITCODE}
