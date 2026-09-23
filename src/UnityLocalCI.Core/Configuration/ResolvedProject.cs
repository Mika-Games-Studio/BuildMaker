namespace UnityLocalCI.Core.Configuration;

/// <summary>
/// Um projeto com Defaults ja aplicados e todos os campos resolvidos.
/// Nada no pipeline le ProjectOptions direto: sempre esta forma, para que
/// "herdou de Defaults" nunca precise ser decidido duas vezes.
/// </summary>
public sealed record ResolvedProject(
    string Name,
    RepositoryOptions Repository,
    string? ManualTriggerFile,
    ResolvedWatcher Watcher,
    ResolvedUnity Unity,
    ResolvedPackaging Packaging,
    ResolvedPublishing Publishing,
    ResolvedRetention Retention);

public sealed record ResolvedWatcher(int PollIntervalSeconds, int DebounceSeconds, int HookSignalPort);

public sealed record ResolvedUnity(
    string EditorVersion,
    string BuildTarget,
    string ExecuteMethod,
    int TimeoutMinutes,
    string[] ExtraArgs);

public sealed record ResolvedPackaging(string NamePattern);

public sealed record ResolvedPublishing(
    string StagingFolder,
    string ArtifactFolder,
    bool WriteStatusFiles);

public sealed record ResolvedRetention(int KeepLastBuilds, int MinFreeDiskGb);
