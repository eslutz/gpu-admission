param([Parameter(Mandatory=$true)][string]$Config,[Parameter(Mandatory=$true)][ValidatePattern('^[a-f0-9]{32}$')][string]$TrayRun,[string]$ResolvedLuid)
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
function Get-DockerContainer($Identity) {
    if($Identity -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9_.-]*$'){return}
    try {
        $result=@(& docker inspect --type container --format '{{.Id}} {{.State.Status}}' $Identity 2>$null)
        if($LASTEXITCODE -eq 0 -and $result.Count -eq 1 -and
           $result[0] -cmatch '^([0-9a-f]{64}) ([a-z]+)$') {
            return @{id=$Matches[1];status=$Matches[2]}
        }
    } catch {} # An unreachable engine or missing container is never proof of a stop.
}
$c=Read-Record $Config
if($ResolvedLuid){$c.windows_engine_luid=$ResolvedLuid}
$schema=if($c.protocol){$c.protocol}else{'home-network-gpu-admission/v1'}
$monitorOnly=$c.telemetry -ceq 'monitor-only'
if($c.consumers -isnot [hashtable] -or
   ($schema -ceq 'home-network-gpu-admission/v1' -and ($monitorOnly -or $c.gpu -cnotmatch '^GPU-[0-9a-fA-F-]{36}$')) -or
   ($schema -ceq 'gpu-admission/v2' -and (!$monitorOnly -or $c.gpu -cnotmatch '^pci-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{2}-[a-f0-9]{2}-[a-f0-9]$')) -or
   $schema -cnotin @('home-network-gpu-admission/v1','gpu-admission/v2')){throw 'Invalid config'}
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
    $state=$(if(Test-Path $journal){Read-Record $journal}else{New-GpuAdmissionState $c.gpu $schema})
    if($state.schema -cne $schema -or $state.gpu -cne $c.gpu -or $state.generation -cnotmatch '^[0-9a-f]{32}$'){throw 'Invalid journal; manual reconciliation required'}
    $state.held=$true;$state.low_since=$null;$state.high_samples=0
    $quit=Join-Path $root "quit-$TrayRun"
    $logDirectory=Join-Path $root 'logs'
    $lastEvent=$null
    $lastWrite=[DateTimeOffset]::MinValue
    $lastSuccess=$null
    while($true) {
        $sample=$null;$containers=@{};$requests=@()
        $load=$null;$dedicated=$null;$shared=$null;$probe='windows-counters'
        try {
            if(!$monitorOnly) {
                $probe='nvidia-query'
                $lines=@(& nvidia-smi "--id=$($c.gpu)" --query-gpu=uuid,utilization.gpu,memory.free --format=csv,noheader,nounits 2>$null)
                if($LASTEXITCODE -ne 0 -or $lines.Count -ne 1){throw 'NVIDIA probe unavailable'}
                $parts=$lines[0].Split(',')
                if($parts.Count -ne 3 -or $parts[0].Trim() -cne $c.gpu){throw 'NVIDIA adapter did not match'}
                $free=[double]::Parse($parts[2],[cultureinfo]::InvariantCulture)
                if([double]::IsNaN($free) -or [double]::IsInfinity($free) -or $free -lt 0){throw 'NVIDIA memory unavailable'}
            }
            $probe='windows-counters'
            $counters=Get-Counter -Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop
            $probe='adapter-counters'
            $load=Get-GpuEngineUtilization $counters.CounterSamples $c.windows_engine_luid $c.windows_physical_index
            if(!$monitorOnly){$sample=@{gpu=$c.gpu;utilization=$load;free_vram_mib=$free}}
            $probe=if($monitorOnly){'monitor-only'}else{'ok'}
            $lastSuccess=[DateTimeOffset]::UtcNow.ToString('o')
        } catch {$sample=$null}
        if($monitorOnly) {
            try {
                $memory=(Get-Counter -Counter '\GPU Adapter Memory(*)\Dedicated Usage','\GPU Adapter Memory(*)\Shared Usage' -ErrorAction Stop).CounterSamples
                $instance="luid_$($c.windows_engine_luid)_phys_$($c.windows_physical_index)"
                foreach($reading in $memory) {
                    if($reading.InstanceName -ne $instance -or $reading.Status -ne 0 -or
                       [double]::IsNaN([double]$reading.CookedValue) -or [double]::IsInfinity([double]$reading.CookedValue) -or $reading.CookedValue -lt 0){continue}
                    if($reading.Path -match '\\dedicated usage$'){$dedicated=[math]::Round($reading.CookedValue / 1MB)}
                    if($reading.Path -match '\\shared usage$'){$shared=[math]::Round($reading.CookedValue / 1MB)}
                }
            } catch {} # Display telemetry is optional and never enables admission.
        }
        foreach($consumer in $c.consumers.Keys) {
            $definition=$c.consumers[$consumer]
            $kind=if($definition.kind){$definition.kind}else{'docker'}
            if($consumer -cnotmatch '^[a-z][a-z0-9-]{0,47}$' -or $kind -cnotin @('docker','windows')){throw 'Unsafe consumer'}
            if($kind -ceq 'docker' -and $state.owner.consumer -ceq $consumer -and
               $state.owner.docker_container_id -cmatch '^[0-9a-f]{64}$') {
                $instance=Get-DockerContainer $state.owner.docker_container_id
                if($instance.id -ceq $state.owner.docker_container_id -and $instance.status -cin @('exited','dead')){
                    $containers[$consumer]='stopped'
                }
            }
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
        # Timestamp the decision after telemetry and request I/O. A worker can update a
        # request while those reads are in progress.
        $now=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        if($monitorOnly){$policy='Hold'}
        $eligible=@($requests | Where-Object {$_.state -cne 'requested' -or ($policy -ceq 'Auto' -and $consumerModes[$_.consumer] -ceq 'Auto')})
        $hadOwner=$null -ne $state.owner
        $step=Invoke-GpuAdmissionStep $state $sample $eligible $c.consumers $containers $now
        if(!$hadOwner -and $state.owner.phase -ceq 'reserved') {
            $selected=$c.consumers[$state.owner.consumer]
            if($selected.kind -cne 'windows') {
                $record=$eligible | Where-Object {$_.consumer -ceq $state.owner.consumer -and $_.request_id -ceq $state.owner.request_id} | Select-Object -First 1
                # New clients can provide Docker's default container hostname. Legacy requests still
                # grant, but only a matching release can clear them when the worker goes away.
                if($record.docker_instance -is [string] -and $record.docker_instance -cmatch '^(?:[0-9a-f]{12}|[0-9a-f]{64})$') {
                    $instance=Get-DockerContainer $selected.container
                    if($instance.status -ceq 'running' -and $instance.id.StartsWith($record.docker_instance,[StringComparison]::Ordinal)){
                        $state.owner.docker_container_id=$instance.id
                    }
                }
            }
        }
        if($monitorOnly -and $null -eq $state.owner){$step.reason='monitor-only'}
        # Revoke old public grant before clearing/reassigning journal ownership.
        foreach($file in @(Get-ChildItem "$public\grants" -Filter '*.json')){Remove-Item -LiteralPath $file.FullName -Force}
        Write-Record $journal $state
        $status=@{schema=$state.schema;generation=$state.generation;gpu=$state.gpu;observed_at=$now;mode=$step.mode;reason=$step.reason;policy_mode=$policy;consumer_modes=$consumerModes}
        if($null -ne $step.grant) {
            $grant=$step.grant.Clone();$grant.Remove('docker_container_id');$grant.schema=$state.schema;$grant.observed_at=$now
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
                gpu_utilization_percent=$load; free_vram_mib=$sample.free_vram_mib
                dedicated_used_mib=$dedicated; shared_used_mib=$shared; probe=$probe
                waiting=@($requests | Where-Object {
                    $_.state -cin @('requested','active') -and (Test-GpuRequest $_ $state $_.consumer $now)
                } | ForEach-Object {$_.consumer} | Sort-Object -Unique)
                owner_phase=$state.owner.phase; owner_consumer=$state.owner.consumer; logging=$logging
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
