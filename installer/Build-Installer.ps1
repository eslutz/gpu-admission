param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$PublishDirectory = 'out',
    [string]$Version = '0.0.0-dev'
)
$ErrorActionPreference='Stop'

$root=Split-Path $PSScriptRoot -Parent
$publish=(Resolve-Path -LiteralPath $PublishDirectory).Path
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
foreach($file in 'GpuAdmissionTray.exe','Run-Guard.ps1','GpuAdmission.psm1','hostfxr.dll') {
    if(!(Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)){
        throw 'Publish a self-contained Windows x64 app before building the installer.'
    }
}
[void][IO.Directory]::CreateDirectory($output)
$compiler=Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
if(!(Test-Path -LiteralPath $compiler)) {
    $cache=Join-Path ([IO.Path]::GetTempPath()) ('gpu-admission-inno-'+[guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($cache)
    $download=Join-Path $cache 'innosetup-6.7.4.exe'
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe' -OutFile $download
    if((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ine 'a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c'){
        throw 'Inno Setup checksum mismatch'
    }
    $tool=Join-Path $cache 'compiler'
    $setup=Start-Process -FilePath $download -PassThru -Wait -WindowStyle Hidden -ArgumentList @(
        '/PORTABLE=1','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NOICONS','/NORESTART',('/DIR="'+$tool+'"')
    )
    if($setup.ExitCode){throw "Portable compiler setup exited $($setup.ExitCode)"}
    $compiler=Join-Path $tool 'ISCC.exe'
}
& $compiler /Q "/DAppVersion=$Version" "/DPublishDir=$publish" "/O$output" (Join-Path $root 'installer/GpuAdmission.iss')
if($LASTEXITCODE){exit $LASTEXITCODE}
$binary=Join-Path $output 'GPU-Admission-Setup-x64.exe'
$hash=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  GPU-Admission-Setup-x64.exe" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Built $binary and SHA256SUMS.txt"
