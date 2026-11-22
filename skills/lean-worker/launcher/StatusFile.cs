// One file of a run dir as the status reader takes it in (docs/research/live-status/README.md,
// section 4.1): the run dir is writable by the worker, so its files are hostile input. A file is
// read only when it is a regular file reached without a symbolic link, at most ReadLimit bytes,
// and it is parsed only when it is JSON the reader can use as it is: no duplicate keys, no string
// that is not valid UTF-16, at most MaxDepth levels deep. Every refusal is a fixed reason chosen
// here, never an exception's message (which can carry the file's content).
//
// .NET 8 has no O_NOFOLLOW, O_NONBLOCK or file-type check without P/Invoke (UnixFileMode.TypeMask
// lands in .NET 9), so the checks are layered like GateTrust's:
//   - before the open: the path is not a link (LinkTarget, an lstat plus readlink), not a
//     directory, not a device, and its length is 1..ReadLimit (a FIFO, socket or device has none);
//   - after the open, on the handle: it is seekable (FIFOs, sockets and character devices are
//     not), its length is 1..ReadLimit again, and the path is still not a link;
//   - the read stops at the length the handle reported plus one byte, so a file that keeps giving
//     bytes is refused at the cap whatever its reported length.
// Every one of these steps (the link check, the stat, the open and the read) can block on a FIFO,
// a leased file or a FUSE mount, so a reader runs them all on a thread with a deadline
// (Read(path, budget), or the whole run dir under one deadline in StatusReader): a file that does
// not answer in time is refused, and its thread is left to close what it opened.
// What remains, as races between the checks and the open:
//   - a link swapped in for the file after the first check and swapped back before the second is
//     followed for that one open;
//   - a link swapped in for the run dir (or any directory above it) after the reader checked it is
//     followed for every file read until the reader checks the run dir again after its reads
//     (StatusReader does, and refuses the row then), and it never has to be swapped back for that;
//   - an open through such a link has its side effects on the target: it releases a FIFO's waiting
//     writer, or opens and closes a device.
// What is shown stays what the checks allow: only a regular file (seekable, 1 B to 1 MiB on the
// handle), validated, parsed and cleaned before it is printed.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class StatusFile
{
    /// <summary>
    /// The most bytes read from one file.
    /// </summary>
    public const int ReadLimit = 1024 * 1024;

    /// <summary>
    /// The deepest nesting a status file may have.
    /// </summary>
    public const int MaxDepth = 16;

    private static readonly JsonDocumentOptions _parse = new() { MaxDepth = MaxDepth };
    private static readonly JsonReaderOptions _validate = new() { MaxDepth = MaxDepth };

    /// <summary>
    /// What a read gave: the bytes, nothing (the file is absent) or a problem (a fixed reason).
    /// </summary>
    internal sealed record Content(byte[]? Bytes, string? Problem)
    {
        public bool Missing => Bytes is null && Problem is null;
    }

    /// <summary>
    /// Whether the entry at <paramref name="path"/> is itself a symbolic link (not followed).
    /// </summary>
    public static bool IsLink(string path) => new FileInfo(Path.TrimEndingDirectorySeparator(path)).LinkTarget is not null;

    /// <summary>
    /// The bytes of the regular file at <paramref name="path"/>, or why it is not read, with a
    /// budget of its own (<see cref="ReadBudget.Default"/>).
    /// </summary>
    public static Content Read(string path) => Read(path, ReadBudget.Default());

    /// <summary>
    /// The bytes of the regular file at <paramref name="path"/>, or why it is not read: every step
    /// runs on a thread of its own, waited for as long as <paramref name="budget"/> allows.
    /// </summary>
    public static Content Read(string path, ReadBudget budget)
    {
        try
        {
            return budget.Run(() => ReadHere(path), out Content? content) && content is not null ? content : Refused("did not answer in time");
        }
        catch (IOException)
        {
            return Refused("cannot be read");
        }
    }

    /// <summary>
    /// The read of <see cref="Read(string, ReadBudget)"/> on the calling thread, for a caller that
    /// already runs under a deadline of its own.
    /// </summary>
    internal static Content ReadHere(string path)
    {
        try
        {
            return ReadChecked(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Refused("cannot be read");
        }
    }

    private static Content ReadChecked(string path)
    {
        if (IsLink(path))
        {
            return Refused("is a symbolic link");
        }

        FileInfo info = new(path);
        if (Directory.Exists(path))
        {
            return Refused("is not a regular file");
        }

        if (!info.Exists)
        {
            return new Content(Bytes: null, Problem: null);
        }

        FileAttributes attributes = info.Attributes;
        if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.Device)
            || attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length is 0)
        {
            return Refused("is empty or not a regular file");
        }

        if (info.Length > ReadLimit)
        {
            return Refused("is larger than 1 MiB");
        }

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
        return ReadOpened(path, fs);
    }

    private static Content ReadOpened(string path, FileStream fs)
    {
        if (!fs.CanSeek)
        {
            return Refused("is empty or not a regular file");
        }

        long length = fs.Length;
        if (length is 0)
        {
            return Refused("is empty or not a regular file");
        }

        if (length > ReadLimit)
        {
            return Refused("is larger than 1 MiB");
        }

        if (IsLink(path))
        {
            return Refused("is a symbolic link");
        }

        // One byte more than the handle reported: a file that gives it changed while it was read.
        byte[] buffer = new byte[length + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int n = fs.Read(buffer, total, buffer.Length - total);
            if (n is 0)
            {
                break;
            }

            total += n;
        }

        return total == length ? new Content(buffer[..total], Problem: null) : Refused("changed while it was read");
    }

    private static Content Refused(string problem) => new(Bytes: null, problem);

    /// <summary>
    /// The JSON object in <paramref name="utf8"/>, or null with a fixed <paramref name="problem"/>.
    /// The text is checked first (<see cref="Usable"/>), so the parsed object holds nothing that
    /// throws when it is read or written again.
    /// </summary>
    public static JsonObject? ParseObject(byte[] utf8, out string? problem)
    {
        ReadOnlySpan<byte> text = utf8.AsSpan();
        // A byte order mark (EF BB BF) is skipped, as File.ReadAllText did.
        if (text is [0xEF, 0xBB, 0xBF, ..])
        {
            text = text[3..];
        }

        if (!Usable(text))
        {
            problem = "is not JSON the reader can use";
            return null;
        }

        try
        {
            if (JsonNode.Parse(text, nodeOptions: null, _parse) is JsonObject doc)
            {
                problem = null;
                return doc;
            }
        }
        catch (JsonException)
        {
            problem = "is not JSON the reader can use";
            return null;
        }

        problem = "is not a JSON object";
        return null;
    }

    /// <summary>
    /// Whether the text is one JSON value at most <see cref="MaxDepth"/> deep (counted here, not
    /// left to the parser's option) whose objects have no duplicate key and whose strings and
    /// keys all decode to valid UTF-16.
    /// </summary>
    private static bool Usable(ReadOnlySpan<byte> text)
    {
        Utf8JsonReader reader = new(text, _validate);
        Stack<HashSet<string>> keys = new();
        try
        {
            while (reader.Read())
            {
                if (reader.CurrentDepth > MaxDepth)
                {
                    return false;
                }

                if (reader.TokenType is JsonTokenType.StartObject)
                {
                    keys.Push(new HashSet<string>(StringComparer.Ordinal));
                }
                else if (reader.TokenType is JsonTokenType.EndObject)
                {
                    _ = keys.Pop();
                }
                else if (reader.TokenType is JsonTokenType.PropertyName && !keys.Peek().Add(reader.GetString() ?? string.Empty))
                {
                    return false;
                }
                else if (reader.TokenType is JsonTokenType.String)
                {
                    // Throws on a string that is not valid UTF-16 (a lone surrogate escape).
                    _ = reader.GetString();
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
