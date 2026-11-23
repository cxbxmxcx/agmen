using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using Agmen.Agents;
using Agmen.Services;

namespace Agmen.Hosting;

/// <summary>Immutable, instance-local handoff history. Each file adds one session to its inherited prefix.</summary>
public static class ConversationHistory
{
    private const int HeaderSize = 20; // Parent Guid and its UTF-16 character length.
    private const int MaxPage = 16384;
    private static readonly Encoding TextEncoding = new UnicodeEncoding(false, false, true);
    private static string Folder => AppPaths.PathOf("conversation-history");
    private static string SnapshotPath(Guid id) => Path.Combine(Folder, id.ToString("N") + ".history");

    /// <summary>Freeze complete native records through the byte length observed when the file is opened.</summary>
    public static Guid? Capture(Guid? inherited, AgentKind agent, string? transcriptPath)
    {
        using var transcript = transcriptPath is null ? null : new FileStream(transcriptPath, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        long boundary = transcript?.Length ?? 0;
        var prefixLength = 0;
        if (inherited is Guid parent)
        {
            var segment = Inspect(parent);
            prefixLength = segment.End;
            while (segment.Start > 0) segment = InspectParent(segment);
        }
        if (boundary == 0) return inherited;

        Directory.CreateDirectory(Folder);
        var id = Guid.NewGuid();
        var temporary = Path.Combine(Folder, id.ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Span<byte> header = stackalloc byte[HeaderSize];
                (inherited ?? Guid.Empty).TryWriteBytes(header);
                BinaryPrimitives.WriteInt32LittleEndian(header[16..], prefixLength);
                output.Write(header);
                using (var writer = new StreamWriter(output, TextEncoding, 8192, leaveOpen: true))
                    PortableTranscript.Write(agent, transcript!, boundary, writer);
                if (output.Length == HeaderSize) return inherited;
                if (prefixLength + (output.Length - HeaderSize) / 2 > int.MaxValue)
                    throw new IOException("Handoff history exceeds the supported character offset range.");
            }
            File.Move(temporary, SnapshotPath(id));
            return id;
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>Release a capture that its caller has not published to any tab or saved state.</summary>
    internal static void DiscardCapture(Guid? result, Guid? inherited)
    {
        if (result is not Guid id || result == inherited) return;
        try { File.Delete(SnapshotPath(id)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Unable to discard unpublished handoff {id}: {e.Message}");
        }
    }

    /// <summary>Return a bounded page and its next UTF-16 cursor. A page never cuts a surrogate pair.</summary>
    public static string Read(Guid? history, int offset, int count)
    {
        if (history is not Guid id) return Page(0, "", 0);
        var segment = Inspect(id);
        int total = segment.End;
        offset = Math.Clamp(offset, 0, total);
        int wanted = Math.Min(total - offset, Math.Clamp(count, 1, MaxPage));
        if (wanted == 0) return Page(offset, "", total);

        // Include the previous character so an arbitrary offset inside a pair can be normalized.
        int start = Math.Max(0, offset - 1);
        int end = offset + wanted;
        if (wanted == 1 && end < total) end++; // A single supplementary character requires two units.
        var bytes = new byte[(end - start) * 2];
        while (true)
        {
            int from = Math.Max(start, segment.Start);
            int until = Math.Min(end, segment.End);
            if (until > from)
            {
                using var file = File.OpenRead(SnapshotPath(segment.Id));
                file.Position = HeaderSize + 2L * (from - segment.Start);
                file.ReadExactly(bytes.AsSpan(2 * (from - start), 2 * (until - from)));
            }
            if (segment.Start <= start) break;
            segment = InspectParent(segment);
        }

        // Read units directly because the spare units at either side can themselves be partial pairs.
        char Unit(int position) => (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2 * (position - start)));
        if (offset > start && char.IsLowSurrogate(Unit(offset))) offset--;
        end = offset + wanted;
        if (char.IsHighSurrogate(Unit(end - 1))) end += end == offset + 1 ? 1 : -1;
        string text = TextEncoding.GetString(bytes, 2 * (offset - start), 2 * (end - offset));
        return Page(offset, text, total);
    }

    private static string Page(int offset, string text, int total)
        => JsonSerializer.Serialize(new { offset, next_offset = offset + text.Length, end = offset + text.Length == total, text });

    private sealed record Segment(Guid Id, Guid Parent, int Start, int End);

    private static Segment InspectParent(Segment segment)
    {
        var parent = Inspect(segment.Parent);
        if (parent.End != segment.Start) throw new InvalidDataException("Invalid handoff history prefix.");
        return parent;
    }

    private static Segment Inspect(Guid id)
    {
        using var file = File.OpenRead(SnapshotPath(id));
        Span<byte> header = stackalloc byte[HeaderSize];
        file.ReadExactly(header);
        var parent = new Guid(header[..16]);
        int start = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        long bytes = file.Length - HeaderSize;
        long end = start + bytes / 2;
        if (start < 0 || (start == 0) != (parent == Guid.Empty) || bytes <= 0 || (bytes & 1) != 0 || end > int.MaxValue)
            throw new InvalidDataException("Invalid handoff history file.");
        return new Segment(id, parent, start, (int)end);
    }
}
