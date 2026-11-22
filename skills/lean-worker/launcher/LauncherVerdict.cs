namespace LeanWorker;

/// <summary>
/// The verdict over a recorded launcher: alive, gone (with whether it was another boot) or unknown,
/// with the reason when it is unknown.
/// </summary>
internal readonly record struct LauncherVerdict(string Liveness, bool OtherBoot, string? Reason);
