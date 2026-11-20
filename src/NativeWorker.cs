using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

static class NativeWorker
{
    public static int Run(string root, string id, string[] arguments)
    {
        if (!AdmissionFiles.WorkerId().IsMatch(id)) return 2;
        var config = AdmissionFiles.Read(Path.Combine(root, "bin", "config.json"));
        if (config?["consumers"]?[id] is not JsonObject worker ||
            AdmissionFiles.Value(worker, "kind") != "windows") return 2;
        string executable = AdmissionFiles.Value(worker, "executable");
        if (!Path.IsPathFullyQualified(executable) ||
            !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable) || Path.GetFileName(executable).Equals("wsl.exe", StringComparison.OrdinalIgnoreCase)) return 2;
        var stored = (worker["arguments"] as JsonArray)?.Select(v => v?.GetValue<string>() ?? "").ToArray() ?? [];
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant() + "|" + id)));
        using var workerMutex = new Mutex(true, @"Local\GpuAdmission.Worker." + digest, out bool first);
        if (!first) return 2;

        string directory = Path.Combine(root, "requests", id);
        AdmissionFiles.DirectoryFor(directory);
        string session = Guid.NewGuid().ToString("N");
        string statusPath = Path.Combine(root, "public", "status.json");
        string grantPath = Path.Combine(root, "public", "grants", id + ".json");
        string requestPath = Path.Combine(directory, session + ".json");
        JsonObject? request = null;
        WindowsJob? child = null;
        bool canRelease = true;
        try
        {
            // Waiting is bounded so schedulers can handle temporarily unavailable GPU work.
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromMinutes(10))
            {
                var status = AdmissionFiles.Read(statusPath);
                if (status is null || !Eligible(status, id) ||
                    AdmissionFiles.Value(config, "gpu") != AdmissionFiles.Value(status, "gpu"))
                {
                    if (request is not null) return 3;
                    Thread.Sleep(1000);
                    continue;
                }
                if (request is not null && !AdmissionFiles.Same(request, status, "schema", "gpu", "generation"))
                    return 3;
                request ??= new JsonObject
                {
                    ["schema"] = AdmissionFiles.Value(status, "schema"),
                    ["gpu"] = AdmissionFiles.Value(status, "gpu"),
                    ["generation"] = AdmissionFiles.Value(status, "generation"),
                    ["consumer"] = id, ["request_id"] = session, ["state"] = "requested",
                    ["minimum_free_vram_mib"] = worker["minimum_free_vram_mib"]?.DeepClone() ?? JsonValue.Create(0)
                };
                PublishOptionalPresence(directory, status, id, session, "waiting");
                var grant = AdmissionFiles.Read(grantPath);
                if (Match(status, grant, request, id, "reserved")) request["state"] = "active";
                WriteRequest(requestPath, request);
                if (AdmissionFiles.Value(request, "state") == "active" && Match(status, grant, request, id, "active"))
                    break;
                Thread.Sleep(1000);
            }
            if (request is null || AdmissionFiles.Value(request, "state") != "active" || !Match(AdmissionFiles.Read(statusPath), AdmissionFiles.Read(grantPath), request, id, "active"))
                return 3;
            child = new WindowsJob(executable, stored.Concat(arguments).ToArray());
            canRelease = false;
            if (!PrivatePolicyAllows(root, id) ||
                !Match(AdmissionFiles.Read(statusPath), AdmissionFiles.Read(grantPath), request, id, "active"))
            {
                child.Stop();
                canRelease = WaitEmpty(child);
                return 3;
            }
            child.Start();
            while (!child.IsEmpty())
            {
                try
                {
                    WriteRequest(requestPath, request);
                    PublishOptionalPresence(directory, request, id, session, "running");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { /* Missing heartbeats never clear ownership. */ }
                Thread.Sleep(1500);
            }
            canRelease = true;
            return child.ExitCode;
        }
        finally
        {
            child?.Dispose();
            if (canRelease && request is not null)
            {
                request["state"] = "released";
                try { WriteRequest(requestPath, request); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                PublishOptionalPresence(directory, request, id, session, "idle");
            }
        }
    }

    public static bool Eligible(JsonObject status, string id) =>
        AdmissionFiles.Fresh(status) &&
        (AdmissionFiles.Value(status, "schema") is AdmissionFiles.V1 or AdmissionFiles.V2) &&
        AdmissionFiles.Value(status, "policy_mode") == "Auto" &&
        AdmissionFiles.Value(status["consumer_modes"], id) == "Auto";

    public static bool Match(JsonObject? status, JsonObject? grant, JsonObject request, string id, string phase) =>
        status is not null && grant is not null && Eligible(status, id) && AdmissionFiles.Fresh(grant) &&
        AdmissionFiles.Same(grant, request, "schema", "gpu", "generation", "consumer", "request_id") &&
        AdmissionFiles.Same(status, request, "schema", "gpu", "generation", "consumer", "request_id") &&
        AdmissionFiles.Value(grant, "phase") == phase;

    static bool WaitEmpty(WindowsJob job)
    {
        for (int i = 0; i < 40; i++) { if (job.IsEmpty()) return true; Thread.Sleep(125); }
        return false;
    }
    static bool PrivatePolicyAllows(string root, string id)
    {
        var modes = AdmissionFiles.Read(Path.Combine(root, "modes.json"));
        return AdmissionFiles.Value(modes, "policy_mode") == "Auto" &&
            AdmissionFiles.Value(modes?["consumer_modes"], id) == "Auto";
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static void WriteRequest(string path, JsonObject request)
    {
        var now = Now();
        request["observed_at"] = now;
        request["heartbeat_at"] = now;
        AdmissionFiles.Write(path, request);
    }
    static void PublishOptionalPresence(string directory, JsonObject status, string id, string session, string state)
    {
        try { PublishPresence(directory, status, id, session, state); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void PublishPresence(string directory, JsonObject status, string id, string session, string state)
    {
        var target = Path.Combine(directory, "presence");
        AdmissionFiles.DirectoryFor(target);
        AdmissionFiles.Write(Path.Combine(target, "heartbeat.json"), new JsonObject
        {
            ["schema"] = "gpu-admission-presence/v1", ["consumer"] = id,
            ["session_id"] = session, ["phase"] = state, ["observed_at"] = Now(),
            ["generation"] = AdmissionFiles.Value(status, "generation")
        });
    }

    public static int Probe(string root, string id)
    {
        if (!AdmissionFiles.WorkerId().IsMatch(id)) return 2;
        var status = AdmissionFiles.Read(Path.Combine(root, "public", "status.json"));
        if (AdmissionFiles.Value(AdmissionFiles.Read(Path.Combine(root, "bin", "config.json"))?["consumers"]?[id], "kind") != "windows") return 2;
        if (status is null || !AdmissionFiles.Fresh(status) ||
            AdmissionFiles.Value(status, "schema") is not (AdmissionFiles.V1 or AdmissionFiles.V2) ||
            status["consumer_modes"] is not JsonObject modes || !modes.ContainsKey(id)) return 3;
        PublishPresence(Path.Combine(root, "requests", id), status, id, Guid.NewGuid().ToString("N"), "idle");
        return 0;
    }
}
