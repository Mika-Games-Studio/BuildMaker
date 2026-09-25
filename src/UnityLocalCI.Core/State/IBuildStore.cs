namespace UnityLocalCI.Core.State;

public interface IBuildStore
{
    Task InitializeAsync(CancellationToken ct);

    Task<long> CreateAsync(BuildRecord record, CancellationToken ct);
    Task UpdateAsync(BuildRecord record, CancellationToken ct);
    Task<BuildRecord?> GetAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<BuildRecord>> GetByStatusAsync(BuildStatus status, CancellationToken ct);
    Task<IReadOnlyList<BuildRecord>> GetRecentAsync(string project, int count, CancellationToken ct);
    Task<BuildRecord?> GetLastFinishedAsync(string project, CancellationToken ct);

    /// <summary>
    /// Apaga uma build do historico. Retorna falso quando ela nao existe ou
    /// ainda esta em execucao — apagar o registro de uma build viva deixaria o
    /// pipeline escrevendo num registro que nao existe mais.
    ///
    /// So o registro sai: o log em disco e o artefato ja publicado ficam onde
    /// estao. Quem apaga uma linha da tela quer limpar a tela, nao jogar fora um
    /// zip que o time pode estar usando.
    /// </summary>
    Task<bool> DeleteAsync(long id, CancellationToken ct);

    /// <summary>
    /// Apaga do historico tudo que ja terminou, de um projeto ou de todos.
    /// Retorna quantas linhas sairam. O que esta em execucao ou na fila fica.
    /// </summary>
    Task<int> DeleteFinishedAsync(string? project, CancellationToken ct);

    Task<string?> GetWatcherValueAsync(string project, string field, CancellationToken ct);
    Task SetWatcherValueAsync(string project, string field, string? value, CancellationToken ct);
}

public static class WatcherFields
{
    public const string LastSeenSha = "last_seen_sha";
    public const string LastBuiltSha = "last_built_sha";
}
