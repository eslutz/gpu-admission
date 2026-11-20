using System.Diagnostics;
using System.Text.Json.Nodes;

if (args is ["appearance-contract", var appearanceDirectory])
{
    AppearanceChecks.Run(appearanceDirectory);
    return;
}
if (args is ["ui-preview", var previewDirectory, var iconPath])
{
    AppearanceChecks.Preview(previewDirectory, iconPath);
    return;
}
if (args is ["adapters"])
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(await GpuDiscovery.Detect()));
    return;
}
if (args is ["runner", var profile])
{
    Environment.Exit(NativeWorker.Run(profile, "win-worker", []));
}
if (args is ["crash-workload", var crashMarker])
{
    File.WriteAllText(crashMarker + ".tmp", Environment.ProcessId.ToString());
    File.Move(crashMarker + ".tmp", crashMarker);
    Thread.Sleep(TimeSpan.FromMinutes(2));
    return;
}
if (args is ["exit-259"])
{
    Environment.Exit(259);
}
if (args is ["child", var childMarker])
{
    Thread.Sleep(2500);
    File.WriteAllText(childMarker, "child completed");
    return;
}
if (args is ["parent", var childOutput])
{
    var launch = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    launch.ArgumentList.Add("child");
    launch.ArgumentList.Add(childOutput);
    using var descendant = Process.Start(launch);
    return;
}

static void Assert(bool ok, string context)
{
    if (!ok) throw new InvalidOperationException(context);
}
static void Wait(Func<bool> success, string context)
{
    var time = Stopwatch.StartNew();
    while (time.Elapsed < TimeSpan.FromSeconds(15))
    {
        if (success()) return;
        Thread.Sleep(80);
    }
    throw new InvalidOperationException(context);
}

var root = Path.Combine(Path.GetTempPath(), "gpu-admission-dotnet-" + Guid.NewGuid().ToString("N"));
var configDir = Path.Combine(root, "bin");
var published = Path.Combine(root, "public");
var requests = Path.Combine(root, "requests", "win-worker");
try
{
    Directory.CreateDirectory(root);
    AppearanceChecks.Run(root);
    using (var job = new WindowsJob(Environment.ProcessPath!, ["exit-259"]))
    {
        job.Start();
        Wait(job.IsEmpty, "fast Windows job did not exit");
        Assert(job.ExitCode == 259, "empty job must report the root process exit code");
    }
    foreach (var directory in new[] { configDir, Path.Combine(published, "grants"), requests })
        AdmissionFiles.DirectoryFor(directory);
    var marker = Path.Combine(root, "a file with spaces");
    var gpu = "GPU-00000000-0000-0000-0000-000000000000";
    var generation = Guid.NewGuid().ToString("N");
    var statusPath = Path.Combine(published, "status.json");
    var grantPath = Path.Combine(published, "grants", "win-worker.json");
    var configPath = Path.Combine(configDir, "config.json");
    double Stamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
    var status = new JsonObject
    {
        ["schema"] = AdmissionFiles.V1, ["gpu"] = gpu, ["generation"] = generation,
        ["policy_mode"] = "Hold", ["consumer_modes"] = new JsonObject { ["win-worker"] = "Auto" },
        ["observed_at"] = Stamp()
    };
    AdmissionFiles.Write(configPath, new JsonObject
    {
        ["gpu"] = gpu,
        ["consumers"] = new JsonObject { ["win-worker"] = new JsonObject
        {
            ["kind"] = "windows", ["executable"] = Environment.ProcessPath!,
            ["arguments"] = new JsonArray("parent", marker)
        }}
    });
    AdmissionFiles.Write(statusPath, status);
    Assert(NativeWorker.Probe(root, "win-worker") == 0, "connection probe in Hold");
    Assert(!NativeWorker.Eligible(status, "win-worker"), "Hold is ineligible");
    var runner = Task.Run(() => NativeWorker.Run(root, "win-worker", []));
    Thread.Sleep(350);
    Assert(!Directory.EnumerateFiles(requests, "*.json").Any(), "Held worker must not request GPU");
    status["policy_mode"] = "Auto";
    status["observed_at"] = Stamp() - 16;
    Assert(!NativeWorker.Eligible(status, "win-worker"), "stale guard status is ineligible");
    status["observed_at"] = Stamp();
    AdmissionFiles.Write(Path.Combine(root, "modes.json"), new JsonObject
        { ["policy_mode"] = "Auto", ["consumer_modes"] = new JsonObject { ["win-worker"] = "Auto" } });
    AdmissionFiles.Write(statusPath, status);
    Wait(() => Directory.EnumerateFiles(requests, "*.json").Any(), "worker did not submit its request");
    var path = Directory.GetFiles(requests, "*.json").Single();
    var request = AdmissionFiles.Read(path)!;
    status["request_id"] = AdmissionFiles.Value(request, "request_id");
    status["consumer"] = "win-worker";
    var grant = (JsonObject)request.DeepClone();
    void Grant(string phase)
    {
        status["observed_at"] = Stamp();
        grant["observed_at"] = Stamp();
        grant["phase"] = phase;
        AdmissionFiles.Write(grantPath, grant);
        AdmissionFiles.Write(statusPath, status);
    }
    Grant("reserved");
    Wait(() => AdmissionFiles.Value(AdmissionFiles.Read(path), "state") == "active", "worker did not acknowledge reservation");
    Assert(!File.Exists(marker), "work started before active grant");
    Grant("active");
    var presence = Path.Combine(requests, "presence", "heartbeat.json");
    Wait(() => AdmissionFiles.Value(AdmissionFiles.Read(presence), "phase") == "running", "runner did not launch job");
    Thread.Sleep(800);
    Assert(AdmissionFiles.Value(AdmissionFiles.Read(path), "state") == "active", "parent exit released grant before descendant");
    Assert(runner.Wait(TimeSpan.FromSeconds(15)), "runner did not finish");
    Assert(runner.Result == 0 && File.Exists(marker), "real job or its descendant did not finish");
    Assert(AdmissionFiles.Value(AdmissionFiles.Read(path), "state") == "released", "tree exit did not publish release");
    Console.WriteLine("PASS: Hold probe, freshness, reservation, actual Windows Job Object tree and release");

    File.Delete(path);
    File.Delete(marker);
    status.Remove("consumer");
    status.Remove("request_id");
    status["observed_at"] = Stamp();
    AdmissionFiles.Write(statusPath, status);
    var waiting = Task.Run(() => NativeWorker.Run(root, "win-worker", []));
    Wait(() => Directory.EnumerateFiles(requests, "*.json").Any(), "second worker did not submit its request");
    path = Directory.GetFiles(requests, "*.json").Single();
    request = AdmissionFiles.Read(path)!;
    status["request_id"] = AdmissionFiles.Value(request, "request_id");
    status["consumer"] = "win-worker";
    grant = (JsonObject)request.DeepClone();
    Grant("reserved");
    Wait(() => AdmissionFiles.Value(AdmissionFiles.Read(path), "state") == "active", "worker did not acknowledge second reservation");
    status["policy_mode"] = "Hold";
    status["observed_at"] = Stamp();
    AdmissionFiles.Write(statusPath, status);
    Assert(waiting.Wait(TimeSpan.FromSeconds(15)) && waiting.Result == 3, "Hold did not defer a reserved job");
    Assert(!File.Exists(marker) && AdmissionFiles.Value(AdmissionFiles.Read(path), "state") == "released",
        "unstarted reservation did not release");
    Console.WriteLine("PASS: Hold during reservation deferred the unstarted job and safely released the request");

    File.Delete(path);
    var config = AdmissionFiles.Read(configPath)!;
    config["consumers"]!["win-worker"]!["arguments"] = new JsonArray("crash-workload", marker);
    AdmissionFiles.Write(configPath, config);
    status["policy_mode"] = "Auto";
    status.Remove("consumer");
    status.Remove("request_id");
    status["observed_at"] = Stamp();
    AdmissionFiles.Write(statusPath, status);
    var crashStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    crashStart.ArgumentList.Add("runner");
    crashStart.ArgumentList.Add(root);
    using (var crashing = Process.Start(crashStart) ?? throw new IOException("runner did not start"))
    try
    {
        Wait(() => Directory.EnumerateFiles(requests, "*.json").Any(), "crash fixture did not submit its request");
        path = Directory.GetFiles(requests, "*.json").Single();
        request = AdmissionFiles.Read(path)!;
        status["request_id"] = AdmissionFiles.Value(request, "request_id");
        status["consumer"] = "win-worker";
        grant = (JsonObject)request.DeepClone();
        Grant("reserved");
        Wait(() => AdmissionFiles.Value(AdmissionFiles.Read(path), "state") == "active", "crash fixture did not reserve");
        Grant("active");
        Wait(() => File.Exists(marker) && File.ReadAllText(marker).Length > 0, "crash fixture did not start inside Job Object");
        using var nativeChild = Process.GetProcessById(int.Parse(File.ReadAllText(marker)));
        _ = nativeChild.Handle; // Keep the OS handle across termination.
        crashing.Kill();
        Assert(crashing.WaitForExit(5000) && nativeChild.WaitForExit(5000), "kernel did not close out the crashed runner's child");
        Assert(AdmissionFiles.Value(AdmissionFiles.Read(path), "state") == "active", "a crash fabricated a release");
    }
    finally
    {
        if (!crashing.HasExited) { crashing.Kill(); crashing.WaitForExit(); }
    }
    Console.WriteLine("PASS: runner crash terminated the contained process and left the active request for recovery");

    var freshProfile = Path.Combine(root, "fresh-profile");
    SetupWindow.Provision(freshProfile,
        new GpuAdapter("Test adapter", 0x10de, 0x12, "0x00000000_0x00001001", "01:00.0", false, gpu));
    var freshModes = AdmissionFiles.Read(Path.Combine(freshProfile, "modes.json"));
    Assert(AdmissionFiles.Value(freshModes, "policy_mode") == "Hold" &&
        freshModes?["consumer_modes"] is JsonObject policies && policies.Count == 0,
        "first run did not create an empty, held profile");
    Assert(new DirectoryInfo(freshProfile).GetAccessControl().AreAccessRulesProtected, "default profile inherited broad access");
    var savedConfig = File.ReadAllBytes(Path.Combine(freshProfile, "bin", "config.json"));
    bool duplicateBlocked = false;
    try
    {
        SetupWindow.Provision(freshProfile,
            new GpuAdapter("Other adapter", 0x8086, 0x12, "0x00000000_0x00001111", "02:00.0", false, null));
    }
    catch (IOException) { duplicateBlocked = true; }
    Assert(duplicateBlocked && savedConfig.SequenceEqual(File.ReadAllBytes(Path.Combine(freshProfile, "bin", "config.json"))),
        "setup overwrote an existing profile");
    Console.WriteLine("PASS: fresh setup created a private profile in Hold and preserved existing configuration");

    var adapters = await GpuDiscovery.Detect();
    Assert(adapters.All(a => a.Luid.StartsWith("0x") && !string.IsNullOrWhiteSpace(a.Name)), "invalid native adapter enumeration");
    Console.WriteLine($"PASS: enumerated {adapters.Count} hardware adapter(s), {adapters.Count(x => x.CanUseNvidia)} matched NVIDIA UUID(s)");
}
finally
{
    if (Directory.Exists(root) && Path.GetDirectoryName(root) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) &&
        Path.GetFileName(root).StartsWith("gpu-admission-dotnet-", StringComparison.Ordinal))
        Directory.Delete(root, recursive: true);
}
