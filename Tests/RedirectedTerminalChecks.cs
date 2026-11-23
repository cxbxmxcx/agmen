using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Agmen.Hosting;
using Agmen.Services;

namespace Agmen.Tests;

/// <summary>Launch the production GUI from a caller with pipes, then inspect all three handles
/// inside a real ConPTY tab. Interactive CLIs rely on these handles to detect the terminal.</summary>
internal static class RedirectedTerminalChecks
{
    private static string TestExe => Path.Combine(AppContext.BaseDirectory, "Agmen.Tests.exe");

    internal static void Run()
    {
        string marker = AppPaths.PathOf("terminal-handles.txt");
        var start = new ProcessStartInfo(TestExe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("--terminal-stdio-host");
        start.ArgumentList.Add(marker);
        using var host = Process.Start(start)!;
        host.StandardInput.Close();
        var output = host.StandardOutput.ReadToEndAsync();
        var errors = host.StandardError.ReadToEndAsync();
        if (!host.WaitForExit(20000))
        {
            host.Kill(entireProcessTree: true);
            throw new TimeoutException("The GUI terminal check did not complete.");
        }
        Task.WaitAll(output, errors);
        if (host.ExitCode != 0 || !File.Exists(marker))
            throw new Exception($"The redirected GUI did not start its terminal child. {errors.Result}");
        string redirects = File.ReadAllText(marker);
        if (redirects != "False,False,False")
            throw new Exception($"A hosted agent inherited redirected stdin, stdout or stderr: {redirects}.");
    }

    internal static void RunHost(string marker)
    {
        // Exercise Program.Main, including mode selection, without depending on an installed agent.
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            var tab = new HostedTab(Path.GetDirectoryName(marker)!, null);
            var probe = new Window { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            try
            {
                probe.Content = tab.StartTerminal($"\"{TestExe}\" --terminal-stdio-child \"{marker}\"");
                probe.Show();
                var elapsed = Stopwatch.StartNew();
                while (!File.Exists(marker) && elapsed.Elapsed < TimeSpan.FromSeconds(10))
                    await Task.Delay(50);
            }
            finally
            {
                tab.Stop();
                probe.Close();
                Application.Current.Shutdown();
            }
        }, DispatcherPriority.Background);
        Environment.ExitCode = Agmen.Program.Main(["--workspace", "Terminal check"]);
    }
}
