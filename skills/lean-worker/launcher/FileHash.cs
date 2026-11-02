// The state of a file the launcher watches: the hash of its content, or the marker of a file it
// could not read. The watch of the working tree and the watch of the repository's own git
// metadata hash their files the same way, so the marker, the mode and the unreadable entry live
// in one place.

using System.Globalization;
using System.Security.Cryptography;

namespace LeanWorker;

/// <summary>
/// The state of a file the launcher watches: the hash of its content — with its mode, when the
/// watcher cares about it — or the entry of a file the launcher could not read.
/// </summary>
internal static class FileHash
{
    /// <summary>
    /// The marker of a file the launcher could not read: it differs from every hash (and from
    /// <c>"-"</c>, deleted), so a file that is readable in only one of the two snapshots is
    /// reported as changed.
    /// </summary>
    internal const char Marker = '?';

    /// <summary>
    /// The state of a file: the hash of its content, or the entry of a file the launcher could not
    /// read. The mode is part of the hash when <paramref name="withMode"/> is set: an exec bit
    /// added to a hook counts as a change, and the hook runs what the bit made it run.
    /// </summary>
    internal static async Task<string> OfAsync(string path, bool withMode = false)
    {
        try
        {
            byte[] content = await File.ReadAllBytesAsync(path, CancellationToken.None).ConfigureAwait(false);
            if (!withMode)
            {
                return Content(content);
            }

            byte[] mode = Json.Utf8.GetBytes(Mode(path) + "\0");
            byte[] both = new byte[mode.Length + content.Length];
            Buffer.BlockCopy(mode, 0, both, 0, mode.Length);
            Buffer.BlockCopy(content, 0, both, mode.Length, content.Length);
            return Content(both);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable(path);
        }
    }

    /// <summary>
    /// The state of a link: the hash of the target it points at (a hook that is a link runs what it
    /// points at). The target itself stays out of the summary: only its hash is in it.
    /// </summary>
    internal static string OfLink(string target) => Content(Json.Utf8.GetBytes("link:" + target));

    /// <summary>
    /// Whether a state says the file could not be read: it starts with the marker.
    /// </summary>
    internal static bool IsUnreadable(string state) => state.StartsWith(Marker);

    /// <summary>
    /// The entry of a file the launcher could not read: the marker plus the metadata that is still
    /// readable — the size, the mtime (in ticks) and the unix mode. A file that changed while it
    /// stayed unreadable (a write that kept the mode) changes its size or its mtime, so it counts
    /// as changed instead of slipping through as unchanged. When even the metadata cannot be read,
    /// the entry is the bare marker: nothing could be told about the file at all.
    /// </summary>
    internal static string Unreadable(string path)
    {
        try
        {
            FileInfo info = new(path);
            return string.Create(CultureInfo.InvariantCulture, $"{Marker}|{info.Length}|{File.GetLastWriteTimeUtc(path).Ticks}|{Mode(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Marker.ToString();
        }
    }

    /// <summary>
    /// The unix mode of a file, or <c>"-"</c> where the platform has none (Windows) or the mode
    /// cannot be read.
    /// </summary>
    internal static string Mode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return "-";
        }

        try
        {
            return ((int)File.GetUnixFileMode(path)).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "-";
        }
    }

    /// <summary>
    /// The hash of the bytes of a file.
    /// </summary>
    private static string Content(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
