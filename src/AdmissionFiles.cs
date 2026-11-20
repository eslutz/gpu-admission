using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static partial class AdmissionFiles
{
    public const string V1 = "home-network-gpu-admission/v1";
    public const string V2 = "gpu-admission/v2";
    public static string Root()
    {
        var pointer = Path.Combine(AppContext.BaseDirectory, "root.txt");
        if (!File.Exists(pointer))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpuAdmission");
        var root = File.ReadAllText(pointer).Trim();
        if (!Path.IsPathFullyQualified(root))
            throw new IOException("root.txt must contain an absolute path to the state directory.");
        return Path.GetFullPath(root);
    }

    public static JsonObject? Read(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (source.Length > 16384) return null;
            using var reader = new StreamReader(source, new UTF8Encoding(false, true));
            return JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        { return null; }
    }

    public static void Write(string path, JsonObject data)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(file, data, new JsonSerializerOptions { WriteIndented = true });
                if (file.Length > 16384) throw new IOException("Configuration exceeds the guard's 16 KiB limit.");
                file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }

    public static void DirectoryFor(string path, bool privateRoot = false)
    {
        var directory = new DirectoryInfo(path);
        for (var part = directory; part is not null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Select a local directory without junctions or symbolic links: {part.FullName}");
        if (!privateRoot || directory.Exists)
        {
            directory.Create();
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        var permissions = new DirectorySecurity();
        permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
        {
            permissions.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        directory.Create(permissions);
    }
    public static bool Fresh(JsonObject? record) => Stamp(record) is double age && age >= 0 && age <= 15;
    static double? Stamp(JsonObject? record) => record?["observed_at"] is JsonValue value && value.TryGetValue<double>(out var stamp) &&
        double.IsFinite(stamp) ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d - stamp : null;
    public static string Value(JsonNode? node, string key) => (node as JsonObject)?[key]?.ToString() ?? "";
    public static bool Same(JsonObject a, JsonObject b, params string[] keys) =>
        keys.All(k => Value(a, k).Length > 0 && Value(a, k) == Value(b, k));
    [GeneratedRegex("^[a-z][a-z0-9-]{0,47}$")]
    public static partial Regex WorkerId();
}
