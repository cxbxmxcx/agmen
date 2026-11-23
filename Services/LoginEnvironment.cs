using System.Collections;

namespace Agmen.Services;

/// <summary>
/// Rebuilds this process's environment from the registry, which is what a fresh
/// logon shell would see. Hosted sessions inherit Agmen's environment because
/// the terminal library starts processes without an environment block of its own.
/// Anything Agmen itself inherited from a parent Claude Code session, such as
/// CLAUDE_CODE_CHILD_SESSION, NO_COLOR, or GIT_TERMINAL_PROMPT, would otherwise turn
/// off transcript saving, colors, and git prompts in every tab. Applied at startup
/// and again before each launch, so new tabs pick up changed variables without a restart.
/// </summary>
public static class LoginEnvironment
{
    private static readonly object Gate = new();

    // Per-logon variables that live only in the process environment, never in the registry.
    private static readonly string[] Volatile =
    {
        "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH", "USERNAME", "USERDOMAIN",
        "USERDOMAIN_ROAMINGPROFILE", "COMPUTERNAME", "SESSIONNAME", "LOGONSERVER", "SystemDrive", "SystemRoot",
        "windir", "ALLUSERSPROFILE", "PUBLIC", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "PATHEXT", "ComSpec", "OS",
        "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION",
        "NUMBER_OF_PROCESSORS", "DriverData", "PSModulePath", "OneDrive", "OneDriveCommercial", "OneDriveConsumer",
        "TEMP", "TMP",
    };

    public static (int Removed, int Kept) Apply()
    {
        lock (Gate)
        {
            var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
            {
                foreach (DictionaryEntry e in Environment.GetEnvironmentVariables(scope))
                {
                    var key = (string)e.Key;
                    var value = Environment.ExpandEnvironmentVariables((string?)e.Value ?? "");
                    // Logon semantics: the user PATH is appended to the machine PATH.
                    if (key.Equals("Path", StringComparison.OrdinalIgnoreCase) && target.TryGetValue("Path", out var machinePath))
                        target["Path"] = machinePath.TrimEnd(';') + ";" + value;
                    else
                        target[key] = value;
                }
            }
            foreach (var key in Volatile)
                if (!target.ContainsKey(key) && Environment.GetEnvironmentVariable(key) is { } v)
                    target[key] = v;

            // Workspace identity belongs to this host. A user's logon default can differ
            // when the window was opened from a shortcut or the workspace picker.
            foreach (var key in new[] { "AGMEN_INSTANCE", "AGMEN_WORKSPACE_FAMILY", "AGMEN_WORKSPACE_NAME" })
            {
                if (Environment.GetEnvironmentVariable(key) is { } value) target[key] = value;
                else target.Remove(key);
            }

            int removed = 0;
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                var key = (string)e.Key;
                if (target.ContainsKey(key) || key.StartsWith('=')) continue;
                try { Environment.SetEnvironmentVariable(key, null); removed++; }
                catch (ArgumentException) { /* unremovable pseudo-variable */ }
            }
            foreach (var kv in target)
            {
                try { Environment.SetEnvironmentVariable(kv.Key, kv.Value); }
                catch (ArgumentException) { /* invalid name or oversized value */ }
            }
            return (removed, target.Count);
        }
    }
}
