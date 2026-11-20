using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agmen.Services;

/// <summary>
/// A diagnostic record of what agent CLIs send Agmen: one JSON line per event in probe.jsonl in
/// the instance data folder, written by the hook command and the tool server when the
/// AGMEN_PROBE environment variable is 1. Useful when a new CLI version changes its signals.
/// Several processes append at once, so a busy file is retried briefly.
/// </summary>
public static class ProbeLog
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("AGMEN_PROBE") == "1";

    public static void Write(JsonObject entry)
    {
        entry["at"] = DateTime.Now.ToString("HH:mm:ss.fff");
        entry["pid"] = Environment.ProcessId;
        var line = entry.ToJsonString(Options) + "\n";
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                File.AppendAllText(AppPaths.PathOf("probe.jsonl"), line);
                return;
            }
            catch (IOException) { Thread.Sleep(30); }
            catch (Exception) { return; }   // a probe must never break the process writing it
        }
    }
}
