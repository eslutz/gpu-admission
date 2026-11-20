$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('gpu-admission-test-'+[guid]::NewGuid().ToString('N'))
$run=[guid]::NewGuid().ToString('N')
$generation=[guid]::NewGuid().ToString('N')
$gpu='GPU-00000000-0000-0000-0000-000000000000'
$quit=Join-Path $root "quit-$run"
$child=$null
function Record($name,$data){
    [IO.File]::WriteAllText((Join-Path $root $name),($data|ConvertTo-Json -Depth 8))
}
function Read($name) {
    try { Get-Content -LiteralPath (Join-Path $root $name) -Raw | ConvertFrom-Json } catch {}
}
function Until($condition) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while($timer.Elapsed.TotalSeconds -lt 25){
        if(& $condition){return}
        if($child.HasExited){throw "Unexpected guard exit: $($child.StandardError.ReadToEnd())"}
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out: $condition"
}
try {
    foreach($dir in 'public/grants','requests/tdarr','requests/telecom-transcription','logs') {
        [void][IO.Directory]::CreateDirectory((Join-Path $root $dir))
    }
    $consumers=@{
        tdarr=@{container='test-tdarr';minimum_free_vram_mib=0}
        'telecom-transcription'=@{container='test-telecom';minimum_free_vram_mib=6144}
    }
    Record 'config.json' @{root=$root;gpu=$gpu;consumers=$consumers}
    Record 'modes.json' @{policy_mode='Hold';consumer_modes=@{tdarr='Auto';'telecom-transcription'='Auto'}}
    $owner=@{consumer='tdarr';request_id=[guid]::NewGuid().ToString('N');phase='active';generation=$generation;gpu=$gpu}
    Record 'owner.json' @{schema='home-network-gpu-admission/v1';gpu=$gpu;generation=$generation;owner=$owner;pending=@{};last_sample=$null}
    $old=Join-Path $root 'logs/events-20200101T000000000Z-00000000000000000000000000000000.jsonl'
    [IO.File]::WriteAllText($old,'expired')
    [IO.File]::SetLastWriteTimeUtc($old,[datetime]::UtcNow.AddDays(-31))
    [IO.File]::WriteAllText((Join-Path $root 'logs/do-not-delete.txt'),'unmanaged')
    $guard=Join-Path $PSScriptRoot 'Run-Guard.ps1'
    $wrapper=@'
param($Guard,$Config,$Run,$Marker)
function nvidia-smi {$global:LASTEXITCODE=1}
function docker {
    $global:LASTEXITCODE=0
    if(Test-Path -LiteralPath $Marker){'exited'}else{'running'}
}
& $Guard -Config $Config -TrayRun $Run
'@
    [IO.File]::WriteAllText((Join-Path $root 'fake.ps1'),$wrapper)
    $info=[Diagnostics.ProcessStartInfo]::new()
    $info.FileName=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $info.Arguments="-NoProfile -NonInteractive -File `"$root\fake.ps1`" `"$guard`" `"$root\config.json`" $run `"$root\exited`""
    $info.UseShellExecute=$false
    $info.RedirectStandardInput=$true
    $info.RedirectStandardError=$true
    $info.CreateNoWindow=$true
    $child=[Diagnostics.Process]::Start($info)
    $child.StandardInput.WriteLine($run)
    $child.StandardInput.Close()
    Until { (Read 'public/status.json').reason -eq 'owner-uncertain' }
    [IO.File]::WriteAllText($quit,'')
    $first=(Read 'public/status.json').observed_at
    Until { (Read 'public/status.json').observed_at -gt $first }
    if(!(Test-Path -LiteralPath $quit) -or (Read 'owner.json').owner.request_id -ne $owner.request_id){
        throw 'Quit while ownership is uncertain must keep draining'
    }
    [IO.File]::Move($quit,$quit+'.cancelled')
    [IO.File]::WriteAllText((Join-Path $root 'exited'),'')
    Until { (Read 'public/status.json').reason -eq 'gpu-unavailable' }
    if($child.HasExited -or (Read 'public/status.json').policy_mode -ne 'Hold'){throw 'Cancel quit did not keep Hold running'}
    [IO.File]::WriteAllText($quit,'')
    if(!$child.WaitForExit(25000) -or $child.ExitCode -ne 0){throw 'Idle guard did not exit cleanly'}
    if((Read 'public/status.json').reason -ne 'guard-stopped' -or $null -ne (Read 'owner.json').owner){throw 'Quit did not publish final state'}
    if(Test-Path -LiteralPath $old){throw 'Expired managed archive still exists'}
    if(!(Test-Path -LiteralPath (Join-Path $root 'logs/do-not-delete.txt'))){throw 'Unmanaged file removed'}
    $events=Get-Content -LiteralPath (Join-Path $root 'logs/events.jsonl') | ForEach-Object {$_ | ConvertFrom-Json}
    if($events[0].event -ne 'gpu.admission.start' -or $events[-1].event -ne 'gpu.admission.stop'){throw 'Lifecycle log missing'}
    Write-Output 'PASS: retained uncertain ownership, cancelled quit, clean quit, lifecycle log, archive retention'
} finally {
    if($child){if(!$child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}
    $base=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if((Test-Path -LiteralPath $root) -and
       (Resolve-Path -LiteralPath $root).Path.StartsWith($base,[StringComparison]::OrdinalIgnoreCase) -and
       (Split-Path -Leaf $root) -cmatch '^gpu-admission-test-[a-f0-9]{32}$'){
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
