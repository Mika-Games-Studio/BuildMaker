namespace UnityLocalCI.Core.Git;

public sealed record CommitInfo(string Sha, string Author, string Message)
{
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}

public sealed record SyncOutcome(bool Cloned);

public interface IGitClient
{
    /// <summary>Clona o workspace se ele ainda nao existir. Retorna se houve clone.</summary>
    Task<SyncOutcome> EnsureWorkspaceAsync(GitContext context, CancellationToken ct);

    Task FetchAsync(GitContext context, CancellationToken ct);

    /// <summary>SHA de origin/&lt;branch&gt; apos o fetch.</summary>
    Task<string> GetRemoteHeadShaAsync(GitContext context, CancellationToken ct);

    Task<CommitInfo> GetCommitInfoAsync(GitContext context, string sha, CancellationToken ct);

    /// <summary>reset --hard no sha, clean preservando o cache do Unity e lfs pull quando aplicavel.</summary>
    Task CheckoutAsync(GitContext context, string sha, CancellationToken ct);

    /// <summary>
    /// Branches que existem no remoto, em ordem alfabetica. Serve para a tela de
    /// configuracao oferecer a lista em vez de esperar o nome digitado — nome de
    /// branch errado so falha na primeira build, e falha parecendo outra coisa.
    /// </summary>
    Task<IReadOnlyList<string>> ListRemoteBranchesAsync(GitContext context, CancellationToken ct);
}

/// <summary>Dados por invocacao. O PAT circula aqui e nunca e persistido.</summary>
public sealed record GitContext
{
    public required string WorkspacePath { get; init; }
    public required string RepositoryUrl { get; init; }
    public required string Branch { get; init; }
    /// <summary>PAT em memoria, injetado via http.extraHeader. Nulo quando o repositorio e anonimo.</summary>
    public string? PersonalAccessToken { get; init; }
}
