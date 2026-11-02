// The libc calls of the launcher: the signal to a whole process group and the process group of a
// process.

using System.Runtime.InteropServices;

namespace LeanWorker;

/// <summary>
/// The native calls of the launcher: the signal to a process group (a negative pid addresses the
/// group, not one process) and the group a process is in.
/// </summary>
internal static partial class NativeMethods
{
    // CA5392 asks every P/Invoke for DefaultDllImportSearchPaths, but the attribute cannot be put
    // on a LibraryImport method (its generator fails on it) and its enum is not in the target's
    // reference assemblies. The search path is a Windows concern; the launcher calls libc, which
    // the OS resolves.
#pragma warning disable CA5392
    [LibraryImport("libc", EntryPoint = "kill")]
    internal static partial int Kill(int pid, int sig);

    [LibraryImport("libc", EntryPoint = "getpgid")]
    internal static partial int GetPgid(int pid);
#pragma warning restore CA5392
}
