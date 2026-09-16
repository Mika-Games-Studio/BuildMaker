namespace UnityLocalCI.Core.Queue;

/// <summary>
/// Fila de um projeto: capacidade 1 com politica de substituicao.
///
/// Se ja existe um job aguardando e chega outro, o novo substitui o antigo e o
/// substituido e devolvido ao chamador para ser marcado como Cancelled. Um job
/// em execucao nunca e substituido, porque o consumidor ja o retirou da fila
/// antes de comecar.
/// </summary>
public sealed class ProjectQueue
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private BuildJob? _pending;

    /// <summary>
    /// Se ha um sinal pendente no semaforo. Sem isso, um Enqueue depois de um
    /// Take que nao passou por WaitForPendingAsync soltaria um segundo sinal e
    /// estouraria a contagem maxima do semaforo.
    /// </summary>
    private bool _signalled;

    public ProjectQueue(string projectName) => ProjectName = projectName;

    public string ProjectName { get; }

    public bool HasPending
    {
        get { lock (_gate) return _pending is not null; }
    }

    /// <summary>
    /// Enfileira. Retorna o job substituido, que o chamador deve marcar como
    /// Cancelled, ou nulo quando a fila estava vazia.
    /// </summary>
    public BuildJob? Enqueue(BuildJob job)
    {
        BuildJob? replaced;
        bool shouldSignal;

        lock (_gate)
        {
            replaced = _pending;
            _pending = job;

            // Substituir um job que ja sinalizou nao acrescenta trabalho novo,
            // apenas troca o conteudo da unica vaga.
            shouldSignal = !_signalled;
            if (shouldSignal) _signalled = true;
        }

        if (shouldSignal) _signal.Release();

        return replaced;
    }

    /// <summary>Le o job pendente sem retirar da fila.</summary>
    public BuildJob? Peek()
    {
        lock (_gate) return _pending;
    }

    /// <summary>Aguarda haver algo na fila. Nao retira: o job so sai em <see cref="Take"/>.</summary>
    public async Task WaitForPendingAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct).ConfigureAwait(false);
        lock (_gate) _signalled = false;
    }

    /// <summary>
    /// Retira o job pendente. Chamado no ultimo instante antes de executar, para
    /// que o commit construido seja sempre o mais recente que chegou ate aqui.
    /// </summary>
    public BuildJob? Take()
    {
        lock (_gate)
        {
            var job = _pending;
            _pending = null;
            return job;
        }
    }
}
