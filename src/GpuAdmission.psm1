# Pure policy; no process, filesystem or network access.
function New-GpuAdmissionState($Gpu,$Schema='home-network-gpu-admission/v1') {
    return @{schema=$Schema;generation=[guid]::NewGuid().ToString('N');gpu=$Gpu;owner=$null;held=$true;low_since=$null;high_samples=0;pending=@{};last_sample=$null}
}
function Test-GpuNumber($Value) {
    return ($Value -is [ValueType] -and $Value -isnot [bool] -and ![double]::IsNaN([double]$Value) -and ![double]::IsInfinity([double]$Value))
}
function Test-GpuRequest($R,$State,$Consumer,$Now) {
    return ($R -is [hashtable] -and $R.schema -ceq $State.schema -and $R.consumer -ceq $Consumer -and
        $R.request_id -cmatch '^[0-9a-f]{32}$' -and $R.generation -ceq $State.generation -and $R.gpu -ceq $State.gpu -and
        $R.state -cin @('requested','active','released') -and (Test-GpuNumber $R.observed_at) -and
        (Test-GpuNumber $R.heartbeat_at) -and $Now-$R.observed_at -ge 0 -and ($Now-$R.observed_at -le 15 -or $R.state -ceq 'released') -and
        $Now-$R.heartbeat_at -ge 0 -and ($Now-$R.heartbeat_at -le 15 -or $R.state -ceq 'released') -and
        (Test-GpuNumber $R.minimum_free_vram_mib) -and $R.minimum_free_vram_mib -ge 0)
}
function Invoke-GpuAdmissionStep($State,$Sample,$Requests,$Consumers,$Containers,$Now) {
    $validSample=($Sample -is [hashtable] -and $Sample.gpu -ceq $State.gpu -and
        (Test-GpuNumber $Sample.utilization) -and $Sample.utilization -ge 0 -and $Sample.utilization -le 100 -and
        (Test-GpuNumber $Sample.free_vram_mib) -and $Sample.free_vram_mib -ge 0)
    if ($null -ne $State.last_sample -and ($Now-$State.last_sample -gt 10 -or $Now -lt $State.last_sample)) {
        $State.held=$true; $State.low_since=$null; $State.high_samples=0
    }
    $State.last_sample=$Now
    $records=@{}
    foreach($r in $Requests) {
        if ($Consumers.ContainsKey([string]$r.consumer) -and (Test-GpuRequest $r $State $r.consumer $Now)) {
            $key=$r.consumer+'/'+$r.request_id
            # Reject duplicate identities rather than picking a file by enumeration order.
            if($records.ContainsKey($key)){$records[$key]=$null}else{$records[$key]=$r}
        }
    }
    $pending=@{}
    foreach($key in $records.Keys) {
        $r=$records[$key]
        if($null -ne $r -and $r.state -ceq 'requested') {
            $pending[$key]=$(if($State.pending.ContainsKey($key)){$State.pending[$key]}else{$Now})
        }
    }
    $State.pending=$pending
    # Reservations are still unstarted work: continue external-load hysteresis.
    if($null -ne $State.owner -and $State.owner.phase -cne 'active') {
        if(!$validSample){$State.held=$true;$State.low_since=$null;$State.high_samples=0}
        elseif($Sample.utilization -ge 30){$State.high_samples++;$State.low_since=$null;if($State.high_samples -ge 2){$State.held=$true}}
        elseif($Sample.utilization -lt 15){
            $State.high_samples=0
            if($null -eq $State.low_since){$State.low_since=$Now}
            if($Now-$State.low_since -ge 60){$State.held=$false}
        } else {$State.high_samples=0;$State.low_since=$null}
    }
    $released=$false
    if ($null -ne $State.owner) {
        $o=$State.owner; $key=$o.consumer+'/'+$o.request_id; $r=$records[$key]
        if (($null -ne $r -and $r.state -ceq 'released') -or
            ($Containers.ContainsKey($o.consumer) -and $Containers[$o.consumer] -ceq 'stopped')) {
            $State.owner=$null; $State.held=$true; $State.low_since=$null; $State.high_samples=0; $released=$true
        } else {
            $fresh=($validSample -and $null -ne $r -and $r.state -cin @('requested','active'))
            if($o.phase -ceq 'reserved' -and $State.held){$fresh=$false}
            if($fresh -and $o.phase -ceq 'reserved') {
                $minimum=[math]::Max([double]$r.minimum_free_vram_mib,[double]$Consumers[$o.consumer].minimum_free_vram_mib)
                if($Sample.free_vram_mib -lt $minimum){$fresh=$false}
            }
            if($fresh){$o.phase=$(if($r.state -ceq 'active'){'active'}else{'reserved'})}
            return @{mode=$(if($fresh){'owned'}else{'held'});reason=$(if($fresh){'managed-job'}else{'owner-uncertain'});grant=$(if($fresh){$o}else{$null});state=$State}
        }
    }
    if (!$validSample) {
        $State.held=$true; $State.low_since=$null; $State.high_samples=0
        return @{mode='held';reason='gpu-unavailable';grant=$null;state=$State}
    }
    if ($Sample.utilization -ge 30) {$State.high_samples++} else {$State.high_samples=0}
    if ($State.high_samples -ge 2) {$State.held=$true}
    if ($Sample.utilization -lt 15) {
        if($null -eq $State.low_since){$State.low_since=$Now}
        if($Now-$State.low_since -ge 60){$State.held=$false}
    } else {$State.low_since=$null}
    if($released){$State.low_since=$(if($Sample.utilization -lt 15){$Now}else{$null})}
    if($State.held){return @{mode='held';reason='load-cooldown';grant=$null;state=$State}}
    foreach($key in @($pending.Keys | Sort-Object @{Expression={$pending[$_]}}, @{Expression={$_}})) {
        $r=$records[$key]
        $minimum=[math]::Max([double]$r.minimum_free_vram_mib,[double]$Consumers[$r.consumer].minimum_free_vram_mib)
        if($Sample.free_vram_mib -lt $minimum){continue}
        $State.owner=@{consumer=$r.consumer;request_id=$r.request_id;generation=$State.generation;gpu=$State.gpu;phase='reserved'}
        return @{mode='owned';reason='managed-job';grant=$State.owner;state=$State}
    }
    return @{mode='available';reason='no-eligible-request';grant=$null;state=$State}
}
Export-ModuleMember -Function New-GpuAdmissionState,Invoke-GpuAdmissionStep,Test-GpuRequest

function Get-GpuEngineUtilization($Samples,$Luid,$PhysicalIndex) {
    if($Luid -cnotmatch '^0x[0-9a-fA-F]{8}_0x[0-9a-fA-F]{8}$' -or $PhysicalIndex -lt 0){throw 'Unverified Windows adapter binding'}
    $engines=@{}
    foreach($sample in $Samples) {
        $pattern='^pid_[0-9]+_luid_'+[regex]::Escape($Luid)+'_phys_'+$PhysicalIndex+'_eng_([0-9]+)_engtype_(.+)$'
        if($sample.InstanceName -match $pattern) {
            $key=$Matches[1]+'/'+$Matches[2]
            if($sample.Status -ne 0 -or !(Test-GpuNumber $sample.CookedValue) -or $sample.CookedValue -lt 0){throw 'Invalid engine sample'}
            if(!$engines.ContainsKey($key)){$engines[$key]=0.0}
            $engines[$key]+=[double]$sample.CookedValue
        }
    }
    if(!$engines.Count){throw 'Target adapter counters unavailable'}
    return [math]::Min(100.0,($engines.Values | Measure-Object -Maximum).Maximum)
}
Export-ModuleMember -Function Get-GpuEngineUtilization
