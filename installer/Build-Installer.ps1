param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$PublishDirectory = 'out',
    [string]$Version
)
$ErrorActionPreference='Stop'

$root=Split-Path $PSScriptRoot -Parent
if(!$Version) {
    $project=[xml](Get-Content -LiteralPath (Join-Path $root 'src/GpuAdmissionTray.csproj') -Raw)
    $Version=$project.Project.PropertyGroup.Version
}
if(!$Version){throw 'The app version is missing'}
$publish=(Resolve-Path -LiteralPath $PublishDirectory).Path
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
foreach($file in 'GpuAdmissionTray.exe','Run-Guard.ps1','GpuAdmission.psm1','hostfxr.dll') {
    if(!(Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)){
        throw 'Publish a self-contained Windows x64 app before building the installer.'
    }
}
# Preserve license notices from the exact runtime packs included by dotnet publish.
$deps=Get-Content -LiteralPath (Join-Path $publish 'GpuAdmissionTray.deps.json') -Raw | ConvertFrom-Json -AsHashtable
$assets=Get-Content -LiteralPath (Join-Path $root 'src/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
foreach($entry in $deps.libraries.Keys | Where-Object { $_.StartsWith('runtimepack.') }) {
    $package=$entry.Substring('runtimepack.'.Length)
    $pack=foreach($folder in $assets.packageFolders.Keys) {
        $path=Join-Path $folder $package.ToLowerInvariant()
        if(Test-Path -LiteralPath $path -PathType Container){$path;break}
    }
    if(!$pack){throw "Cannot locate runtime pack $package for its license notices"}
    $notices=@(Get-ChildItem -LiteralPath $pack -File | Where-Object { $_.Name -match '^(LICENSE|THIRD.PARTY.NOTICES)(\.txt)?$' })
    if(!($notices.Name -match '^LICENSE')){throw "Missing runtime license for $package"}
    $noticeDirectory=Join-Path $publish ('licenses/'+$package)
    [void][IO.Directory]::CreateDirectory($noticeDirectory)
    foreach($file in $notices){Copy-Item -LiteralPath $file.FullName -Destination $noticeDirectory -Force}
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
"$hash  GPU-Admission-Setup-x64.exe`n" | Set-Content -LiteralPath (Join-Path $output 'checksum-SHA256.txt') -Encoding ascii -NoNewline
Write-Output "Built $binary and checksum-SHA256.txt"
