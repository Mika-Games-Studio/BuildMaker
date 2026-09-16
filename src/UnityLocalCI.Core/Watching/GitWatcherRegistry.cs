namespace UnityLocalCI.Core.Watching;

/// <summary>
/// Onde os watchers ficam acessiveis por nome de projeto.
///
/// Existe para que o sinal do hook possa cutucar o watcher do projeto certo, em
/// vez de enfileirar direto: toda decisao sobre construir ou nao continua
/// passando pelo watcher, que e quem conhece o debounce e o ultimo sha.
/// </summary>
public sealed class GitWatcherRegistry
{
    private readonly Dictionary<string, GitWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Register(string project, GitWatcher watcher)
    {
        lock (_gate) _watchers[project] = watcher;
    }

    public IReadOnlyList<string> Projects
    {
        get { lock (_gate) return _watchers.Keys.ToArray(); }
    }

    /// <summary>Cutuca um projeto. Retorna false se o nome nao existe.</summary>
    public bool Poke(string project)
    {
        GitWatcher? watcher;
        lock (_gate)
        {
            if (!_watchers.TryGetValue(project, out watcher)) return false;
        }

        watcher.PokeNow();
        return true;
    }

    /// <summary>Cutuca todos. Usado quando o hook nao informa o projeto.</summary>
    public int PokeAll()
    {
        GitWatcher[] all;
        lock (_gate) all = _watchers.Values.ToArray();

        foreach (var watcher in all) watcher.PokeNow();
        return all.Length;
    }
}
