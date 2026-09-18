namespace UnityLocalCI.Core.Configuration;

/// <summary>Aplica Defaults sobre cada projeto e valida o resultado.</summary>
public static class ProjectResolver
{
    // Defaults dos defaults: o que vale quando nem o projeto nem a secao Defaults dizem nada.
    private const int FallbackPollIntervalSeconds = 60;
    private const int FallbackDebounceSeconds = 120;
    private const int FallbackHookSignalPort = 8081;
    private const string FallbackBuildTarget = "WebGL";
    private const string FallbackExecuteMethod = "Builder.PerformBuild";
    private const int FallbackTimeoutMinutes = 90;
    private const string FallbackNamePattern = "{project}-{branch}-{date}-{sha}.zip";
    private const int FallbackKeepLastBuilds = 10;
    private const int FallbackMinFreeDiskGb = 50;

    public static IReadOnlyList<ResolvedProject> ResolveEnabled(CiOptions options)
        => options.Projects.Where(p => p.Enabled).Select(p => Resolve(p, options.Defaults)).ToList();

    /// <summary>
    /// A credencial que este projeto vai usar de verdade: a dele, se tiver, ou a
    /// da maquina. Vazio e nulo valem o mesmo — quem apagou o campo na tela quer
    /// herdar, nao ficar sem credencial.
    /// </summary>
    public static string? ResolveCredential(ProjectOptions project, ProjectDefaults defaults)
    {
        var doProjeto = project.Repository.PatCredentialName;
        if (!string.IsNullOrWhiteSpace(doProjeto)) return doProjeto.Trim();

        var daMaquina = defaults.Repository.PatCredentialName;
        return string.IsNullOrWhiteSpace(daMaquina) ? null : daMaquina.Trim();
    }

    public static ResolvedProject Resolve(ProjectOptions project, ProjectDefaults defaults)
    {
        var w = project.Watcher;
        var dw = defaults.Watcher;
        var u = project.Unity;
        var du = defaults.Unity;
        var pk = project.Packaging;
        var dpk = defaults.Packaging;
        var pb = project.Publishing;
        var dpb = defaults.Publishing;
        var rt = project.Retention;
        var drt = defaults.Retention;

        return new ResolvedProject(
            Name: project.Name,

            // A credencial e a unica coisa do repositorio que se herda: o acesso
            // ao Git e da maquina, e nao de cada jogo. URL, branch e workspace
            // sao necessariamente proprios.
            Repository: new RepositoryOptions
            {
                Url = project.Repository.Url,
                Branch = project.Repository.Branch,
                WorkspacePath = project.Repository.WorkspacePath,
                PatCredentialName = ResolveCredential(project, defaults),
            },
            ManualTriggerFile: project.ManualTriggerFile,
            Watcher: new ResolvedWatcher(
                w?.PollIntervalSeconds ?? dw.PollIntervalSeconds ?? FallbackPollIntervalSeconds,
                w?.DebounceSeconds ?? dw.DebounceSeconds ?? FallbackDebounceSeconds,
                w?.HookSignalPort ?? dw.HookSignalPort ?? FallbackHookSignalPort),
            Unity: new ResolvedUnity(
                // Sem fallback: cada projeto pode estar numa versao diferente do editor
                // e adivinhar aqui produziria build na versao errada em silencio.
                u?.EditorVersion ?? du.EditorVersion ?? "",
                u?.BuildTarget ?? du.BuildTarget ?? FallbackBuildTarget,
                u?.ExecuteMethod ?? du.ExecuteMethod ?? FallbackExecuteMethod,
                u?.TimeoutMinutes ?? du.TimeoutMinutes ?? FallbackTimeoutMinutes,
                u?.ExtraArgs ?? du.ExtraArgs ?? Array.Empty<string>()),
            Packaging: new ResolvedPackaging(
                pk?.NamePattern ?? dpk.NamePattern ?? FallbackNamePattern,
                pk?.IncludeLauncher ?? dpk.IncludeLauncher ?? true),
            Publishing: new ResolvedPublishing(
                pb?.StagingFolder ?? dpb.StagingFolder ?? "",
                pb?.ArtifactFolder ?? dpb.ArtifactFolder ?? "",
                pb?.MaintainLatestFolder ?? dpb.MaintainLatestFolder ?? true,
                pb?.WriteStatusFiles ?? dpb.WriteStatusFiles ?? true),
            Retention: new ResolvedRetention(
                rt?.KeepLastBuilds ?? drt.KeepLastBuilds ?? FallbackKeepLastBuilds,
                rt?.MinFreeDiskGb ?? drt.MinFreeDiskGb ?? FallbackMinFreeDiskGb));
    }
}
