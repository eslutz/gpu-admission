using Microsoft.Win32;

static class StartupPreference
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "GPU Admission";
    static string Command => $"\"{Application.ExecutablePath}\"";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return string.Equals(key?.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled)
        {
            if (Command.Length > 260) throw new InvalidOperationException("The app path is too long for Windows startup.");
            key.SetValue(Name, Command, RegistryValueKind.String);
        }
        else if (string.Equals(key.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase))
        {
            key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
