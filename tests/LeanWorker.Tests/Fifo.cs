using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Named pipes for the tests that need a file a read blocks on: a read that opens a FIFO nobody writes to waits
/// for ever, which is what the reader's deadline is for.
/// </summary>
internal static class Fifo
{
    /// <summary>
    /// Makes a FIFO at <paramref name="path"/> with mkfifo; the test is skipped where that cannot be done.
    /// </summary>
    public static async Task MakeAsync(string path)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "mkfifo is Unix only");
        ProcessStartInfo psi = new("mkfifo") { UseShellExecute = false };
        psi.ArgumentList.Add(path);
        try
        {
            using Process mk = Process.Start(psi)!;
            await mk.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        catch (Win32Exception ex)
        {
            Assert.Skip("mkfifo cannot be run here: " + ex.Message);
        }

        Assert.True(new FileInfo(path).Exists, "mkfifo made no file at " + path);
    }

    /// <summary>
    /// Opens the FIFO for writing and returns the open stream once a reader has opened it (false when none did
    /// within <paramref name="wait"/>): the reader then blocks on a read until the stream is closed, or until it
    /// has data. Disposing the stream releases the reader (it sees the end of the file).
    /// </summary>
    public static async Task<FileStream?> WaitForReaderAsync(string path, TimeSpan wait)
    {
        Task<FileStream> open = Task.Run(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite), TestContext.Current.CancellationToken);
        try
        {
            return await open.WaitAsync(wait, TimeProvider.System, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
