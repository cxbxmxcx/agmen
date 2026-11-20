using System.IO;

namespace Agmen.Services;

/// <summary>Small append-only diagnostic log in the instance data folder, rolled over at about 1 MB.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = AppPaths.PathOf("agmen.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* logging must never take the app down */ }
    }
}
