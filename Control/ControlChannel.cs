using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
    private static readonly string Scope = BuildScope();
    public static string PipeName => $"{AppPaths.InstanceName}.control.{Scope}";
    internal static string HostPipe(int pid) => $"Agmen.host.{pid}.{Scope}";

    private static string BuildScope()
    {
        string user;
        try { user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch (Exception) { user = Environment.UserName; }
        int session = 0;
        try { session = System.Diagnostics.Process.GetCurrentProcess().SessionId; }
        catch (Exception) { /* keep 0 */ }
        return $"{user}.{session}";
    }

    internal static int? NearestHost(IEnumerable<ProcessTree.Node> chain) => chain.Skip(1)
        .FirstOrDefault(n => n.Name.Equals("Agmen", StringComparison.OrdinalIgnoreCase))?.Pid;

    // A provider's global MCP config can override its inherited AGMEN_INSTANCE. Use the
    // actual parent host even when different workspace windows run the same executable.
    internal static async Task<JsonObject> CallHostAsync(string tool, JsonObject args, TimeSpan timeout)
    {
        var chain = ProcessTree.Ancestry(Environment.ProcessId);
        var host = NearestHost(chain) ?? throw new IOException("This process is outside an Agmen workspace.");
        using var cts = new CancellationTokenSource(timeout);
        NamedPipeClientStream pipe;
        bool legacy = false;
        try
        {
            pipe = await ConnectAsync(HostPipe(host), host, 500, cts.Token);
        }
        catch (TimeoutException)
        {
            // Older builds listen only on the instance pipe. Check its server PID before
            // sending anything, including the ancestry those builds require.
            pipe = await ConnectAsync(PipeName, host, 2000, cts.Token);
            legacy = true;
        }
        using (pipe) return await ExchangeAsync(pipe, tool, args, cts.Token, legacy ? chain : null);
    }

    /// <summary>
    /// Send one tool call to the running app. The host identifies the process from the pipe.
    /// </summary>
    public static async Task<JsonObject> CallAsync(string tool, JsonObject args, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await using var pipe = await ConnectAsync(PipeName, null, 2500, cts.Token);
        return await ExchangeAsync(pipe, tool, args, cts.Token);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string name, int? host, int timeout, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout, token);
            if (host.HasValue && (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != host.Value))
                throw new IOException("The control pipe belongs to a different workspace.");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    private static async Task<JsonObject> ExchangeAsync(NamedPipeClientStream pipe, string tool, JsonObject args,
        CancellationToken token, IReadOnlyList<ProcessTree.Node>? legacyChain = null)
    {
        var request = new JsonObject { ["tool"] = tool, ["args"] = args.DeepClone() };
        if (legacyChain != null) request["caller"] = new JsonArray(legacyChain.Select(n => (JsonNode)n.Pid).ToArray());
        await using (var writer = new StreamWriter(pipe, Utf8, 4096, leaveOpen: true) { NewLine = "\n" })
        {
            await writer.WriteLineAsync(request.ToJsonString(Json).AsMemory(), token);
            await writer.FlushAsync(token);
        }

        using var reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
        var line = await reader.ReadLineAsync(token)
                   ?? throw new IOException("Agmen closed the connection without answering.");
        return JsonNode.Parse(line) as JsonObject
               ?? throw new IOException("Agmen sent an answer that isn't readable.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
