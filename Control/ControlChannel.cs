using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agmen.Services;

namespace Agmen.Control;

/// <summary>
/// The local pipe between the running Agmen and the MCP server process that Claude
/// Code starts for the primary session. Each call is one connection carrying one JSON
/// request line and one JSON response line. Only the signed-in user's processes can connect.
/// </summary>
public static class ControlChannel
{
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Compact JSON that keeps non-ASCII text readable for the model.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>One pipe per instance (release or dev), per user, per Windows session.</summary>
    public static string PipeName { get; } = BuildName();

    private static string BuildName()
    {
        string user;
        try { user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch (Exception) { user = Environment.UserName; }
        int session = 0;
        try { session = System.Diagnostics.Process.GetCurrentProcess().SessionId; }
        catch (Exception) { /* keep 0 */ }
        return $"{AppPaths.InstanceName}.control.{user}.{session}";
    }

    /// <summary>
    /// Send one tool call to the running app and wait for its answer. <paramref name="caller"/>
    /// is the process chain above the sender, from itself upwards, which tells the app which tab
    /// the call comes from. Throws <see cref="TimeoutException"/> when Agmen is not running.
    /// </summary>
    public static async Task<JsonObject> CallAsync(string tool, JsonObject args, TimeSpan timeout, IReadOnlyList<int>? caller = null)
    {
        using var cts = new CancellationTokenSource(timeout);
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2500, cts.Token);

        var request = new JsonObject { ["tool"] = tool, ["args"] = args.DeepClone() };
        if (caller is not null) request["caller"] = new JsonArray(caller.Select(pid => (JsonNode)pid).ToArray());
        await using (var writer = new StreamWriter(pipe, Utf8, 4096, leaveOpen: true) { NewLine = "\n" })
        {
            await writer.WriteLineAsync(request.ToJsonString(Json));
            await writer.FlushAsync();
        }

        using var reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
        var line = await reader.ReadLineAsync(cts.Token)
                   ?? throw new IOException("Agmen closed the connection without answering.");
        return JsonNode.Parse(line) as JsonObject
               ?? throw new IOException("Agmen sent an answer that isn't readable.");
    }
}
