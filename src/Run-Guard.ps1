param([Parameter(Mandatory=$true)][string]$Config,[Parameter(Mandatory=$true)][ValidatePattern('^[a-f0-9]{32}$')][string]$TrayRun)
$ErrorActionPreference='Stop'
if([Console]::ReadLine() -cne $TrayRun){exit 1}
Import-Module (Join-Path $PSScriptRoot 'GpuAdmission.psm1') -Force
function ConvertTo-Map($Object) {
    if($null -eq $Object){return $null}
    if($Object -is [System.Management.Automation.PSCustomObject]) {
        $h=@{};foreach($p in $Object.PSObject.Properties){$h[$p.Name]=ConvertTo-Map $p.Value};return $h
    }
    if($Object -is [array]){return ,@($Object | ForEach-Object {ConvertTo-Map $_})}
    return $Object
}
function Read-Record($Path) {
    $f=Get-Item -LiteralPath $Path
    if($f.Length -gt 16384 -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Invalid record'}
    return ConvertTo-Map (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}
function Write-Record($Path,$Value) {
    $Path=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    $tmp=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 16 -Compress))
        $stream=[IO.File]::Open($tmp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        if(Test-Path -LiteralPath $Path){[IO.File]::Replace($tmp,$Path,[NullString]::Value)}else{[IO.File]::Move($tmp,$Path)}
    } finally {if(Test-Path -LiteralPath $tmp){Remove-Item -LiteralPath $tmp -Force}}
}
function Write-GuardEvent($Directory, $Event, $Now) {
    $path=Join-Path $Directory 'events.jsonl'
    $day=$Now.UtcDateTime.Date
    $line=($Event | ConvertTo-Json -Depth 8 -Compress)+[Environment]::NewLine
    if(Test-Path -LiteralPath $path) {
        $file=Get-Item -LiteralPath $path
        if($file.LastWriteTimeUtc.Date -ne $day -or
           $file.Length+[Text.Encoding]::UTF8.GetByteCount($line) -gt 1MB) {
            $archive='events-{0}-{1}.jsonl' -f $file.LastWriteTimeUtc.ToString('yyyyMMddTHHmmssfffZ'),[guid]::NewGuid().ToString('N')
            [IO.File]::Move($path,(Join-Path $Directory $archive))
        }
    }
    [IO.File]::AppendAllText($path,$line,[Text.UTF8Encoding]::new($false))
    $size=(Get-Item -LiteralPath $path).Length
    $archives=@(Get-ChildItem -LiteralPath $Directory -File | Where-Object {
        $_.Name -cmatch '^events-[0-9]{8}T[0-9]{9}Z-[0-9a-f]{32}\.jsonl$' -and
        !($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
    } | Sort-Object LastWriteTimeUtc,Name)
    foreach($archive in $archives){$size+=$archive.Length}
    foreach($archive in $archives) {
        if($archive.LastWriteTimeUtc -lt $Now.UtcDateTime.AddDays(-30) -or $size -gt 20MB) {
            Remove-Item -LiteralPath $archive.FullName -ErrorAction Stop
            $size-=$archive.Length
        }
    }
}
$c=Read-Record $Config
if($c.gpu -cnotmatch '^GPU-[0-9a-fA-F-]{36}$' -or $c.consumers -isnot [hashtable]){throw 'Invalid config'}
$root=$c.root;$public=Join-Path $root 'public';$journal=Join-Path $root 'owner.json'
# Setup must provision protected directories/ACLs first. Never create broad mounts implicitly.
foreach($p in @($root,(Join-Path $root 'logs'),$public,"$public\grants","$root\requests")) {
    $d=Get-Item -LiteralPath $p
    if(!$d.PSIsContainer -or ($d.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe root'}
}
$lock=[IO.File]::Open((Join-Path $root 'guard.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    if(!(Test-Path $journal) -and ((Test-Path "$public\status.json") -or @(Get-ChildItem "$public\grants" -Filter '*.json').Count -gt 0)) {
        throw 'Published state without ownership journal; manual stopped-consumer reconciliation required'
    }
    $state=$(if(Test-Path $journal){Read-Record $journal}else{New-GpuAdmissionState $c.gpu})
    if($state.schema -cne 'home-network-gpu-admission/v1' -or $state.gpu -cne $c.gpu -or $state.generation -cnotmatch '^[0-9a-f]{32}$'){throw 'Invalid journal; manual reconciliation required'}
    $state.held=$true;$state.low_since=$null;$state.high_samples=0
    $quit=Join-Path $root "quit-$TrayRun"
    $logDirectory=Join-Path $root 'logs'
    $lastEvent=$null
    $lastWrite=[DateTimeOffset]::MinValue
    $lastSuccess=$null
    while($true) {
        $now=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds();$sample=$null;$containers=@{};$requests=@()
        try {
            $lines=@(& nvidia-smi "--id=$($c.gpu)" --query-gpu=uuid,utilization.gpu,memory.free --format=csv,noheader,nounits 2>$null)
            if($LASTEXITCODE -eq 0 -and $lines.Count -eq 1) {
                $parts=$lines[0].Split(',')
                if($parts.Count -eq 3){
                    $counters=Get-Counter -Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop
                    $load=Get-GpuEngineUtilization $counters.CounterSamples $c.windows_engine_luid $c.windows_physical_index
                    $sample=@{gpu=$parts[0].Trim();utilization=$load;free_vram_mib=[double]::Parse($parts[2],[cultureinfo]::InvariantCulture)}
                }
            }
        } catch {$sample=$null}
        if($null -ne $sample){$lastSuccess=[DateTimeOffset]::UtcNow.ToString('o')}
        foreach($consumer in $c.consumers.Keys) {
            if($consumer -cnotmatch '^[a-z][a-z0-9-]{0,47}$' -or $c.consumers[$consumer].container -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9_.-]+$'){throw 'Unsafe consumer'}
            try {
                $result=@(& docker inspect --format '{{.State.Status}}' ($c.consumers[$consumer].container) 2>$null)
                if($LASTEXITCODE -eq 0 -and $result.Count -eq 1 -and $result[0] -cin @('exited','dead','created')){$containers[$consumer]='stopped'}
            } catch {} # Unknown never clears ownership.
            $dir=Join-Path "$root\requests" $consumer
            foreach($file in @(Get-ChildItem -LiteralPath $dir -Filter '*.json' -ErrorAction Stop | Select-Object -First 256)) {
                try {
                    $r=Read-Record $file.FullName
                    if($r.consumer -ceq $consumer -and $file.BaseName -ceq $r.request_id){$requests+=,$r}
                } catch {} # Malformed/unreadable records never grant admission.
            }
        }
        $policy='Hold';$consumerModes=@{}
        foreach($consumer in $c.consumers.Keys){$consumerModes[$consumer]='Hold'}
        try {
            $modes=Read-Record "$root\modes.json"
            if($modes.policy_mode -cin @('Auto','Hold')){$policy=$modes.policy_mode}
            foreach($consumer in $c.consumers.Keys) {
                if($modes.consumer_modes[$consumer] -cin @('Auto','Hold')){$consumerModes[$consumer]=$modes.consumer_modes[$consumer]}
            }
        } catch {} # Missing/invalid policy is Hold, never an implicit Auto.
        $eligible=@($requests | Where-Object {$_.state -cne 'requested' -or ($policy -ceq 'Auto' -and $consumerModes[$_.consumer] -ceq 'Auto')})
        $step=Invoke-GpuAdmissionStep $state $sample $eligible $c.consumers $containers $now
        # Revoke old public grant before clearing/reassigning journal ownership.
        foreach($file in @(Get-ChildItem "$public\grants" -Filter '*.json')){Remove-Item -LiteralPath $file.FullName -Force}
        Write-Record $journal $state
        $status=@{schema=$state.schema;generation=$state.generation;gpu=$state.gpu;observed_at=$now;mode=$step.mode;reason=$step.reason;policy_mode=$policy;consumer_modes=$consumerModes}
        if($null -ne $step.grant) {
            $grant=$step.grant.Clone();$grant.schema=$state.schema;$grant.observed_at=$now
            $status.consumer=$grant.consumer;$status.request_id=$grant.request_id
            Write-Record "$public\grants\$($grant.consumer).json" $grant
        }
        Write-Record "$public\status.json" $status
        foreach($r in $requests) {
            if($r.state -ceq 'released' -and (Test-GpuRequest $r $state $r.consumer $now) -and
                ($null -eq $state.owner -or $r.request_id -cne $state.owner.request_id -or $r.consumer -cne $state.owner.consumer)) {
                Remove-Item -LiteralPath "$root\requests\$($r.consumer)\$($r.request_id).json" -Force -ErrorAction SilentlyContinue
            }
        }
        $wall=[DateTimeOffset]::UtcNow
        $stopping=$false
        if($policy -ceq 'Hold' -and $null -eq $state.owner -and (Test-Path -LiteralPath $quit)) {
            $claimed=$quit+'.claimed'
            try {
                [IO.File]::Move($quit,$claimed)
                $stopping=$true
            } catch [IO.FileNotFoundException] { } # Cancel won the rename race.
        }
        if($stopping) {
            # There is no owner and this cycle has already revoked prior grants.
            $status.mode='held'
            $status.reason='guard-stopped'
            Write-Record "$public\status.json" $status
        }
        $signature=@(
            $status.mode,$status.reason,$policy,($null -ne $sample),$state.owner.phase,
            $state.owner.consumer,$state.owner.request_id
            foreach($key in ($consumerModes.Keys | Sort-Object)){"$key=$($consumerModes[$key])"}
        ) -join '|'
        $logging='ok'
        if($stopping -or $signature -cne $lastEvent -or ($wall-$lastWrite).TotalSeconds -ge 300) {
            $event=$status.Clone()
            $event.timestamp=$wall.ToString('o')
            $event.event=if($stopping){'gpu.admission.stop'}elseif($null -eq $lastEvent){'gpu.admission.start'}elseif($signature -cne $lastEvent){'gpu.admission.change'}else{'gpu.admission.heartbeat'}
            $event.probe_ok=($null -ne $sample)
            $event.owner_phase=$state.owner.phase
            try {
                Write-GuardEvent $logDirectory $event $wall
                $lastEvent=$signature
                $lastWrite=$wall
            } catch {$logging="error: $($_.Exception.GetType().Name)"}
        }
        try {
            Write-Record "$root\tray-status.json" @{
                run=$TrayRun; observed_at=$now; last_success_utc=$lastSuccess
                gpu_utilization_percent=$sample.utilization; free_vram_mib=$sample.free_vram_mib
                owner_phase=$state.owner.phase; logging=$logging
            }
        } catch {
            # The private display is optional. Public admission records are authoritative.
        }
        if($stopping) {
            Remove-Item -LiteralPath $claimed -ErrorAction SilentlyContinue
            exit 0
        }
        Start-Sleep -Seconds 5
    }
} finally {$lock.Dispose()}
