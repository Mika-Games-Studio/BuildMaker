using System.Collections.Concurrent;

namespace UnityLocalCI.Core.Pipeline;

/// <summary>
/// O log das builds desta sessao, em memoria.
///
/// Antes cada build deixava um build-N.log e um build-N.unity.log em disco —
/// quarenta e poucos KB por arquivo, dois arquivos por build, para sempre, mais
/// uma copia na pasta de destino. Ninguem os abria: quem quer ver o log abre a
/// janela enquanto a build roda, e um log de build que terminou ha tres semanas
/// nao responde nenhuma pergunta que o resumo do erro ja nao responda.
///
/// Entao o log vive aqui, e acaba quando o programa fecha. O que sobrevive a
/// reinicializacao e o que tem valor depois: o registro da build no banco e o
/// resumo do erro, que e o que aparece na grade.
///
/// Os limites existem porque uma build WebGL despeja dezessete mil linhas e nada
/// impede que dez builds rodem numa tarde. Ambos descartam o mais antigo, que e
/// o menos util: numa build que falhou, o que interessa esta no fim.
/// </summary>
public sealed class BuildLogBuffer
{
    /// <summary>Linhas guardadas por build.</summary>
    public const int MaxLinesPerBuild = 20_000;

    /// <summary>Builds cujo log fica em memoria. Passando disso, a mais antiga sai.</summary>
    public const int MaxBuilds = 12;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, Queue<string>> _porBuild = new();

    /// <summary>Ordem de chegada, para saber qual descartar primeiro.</summary>
    private readonly Queue<long> _ordem = new();

    /// <summary>Comeca (ou recomeca) o log de uma build.</summary>
    public void Comecar(long buildId)
    {
        lock (_gate)
        {
            if (_porBuild.TryRemove(buildId, out _))
            {
                // Refazer a mesma build reaproveita a posicao na ordem em vez de
                // criar uma segunda entrada que nunca sera descartada.
                RemoverDaOrdem(buildId);
            }

            _porBuild[buildId] = new Queue<string>();
            _ordem.Enqueue(buildId);

            while (_ordem.Count > MaxBuilds)
            {
                var antiga = _ordem.Dequeue();
                _porBuild.TryRemove(antiga, out _);
            }
        }
    }

    public void Escrever(long buildId, string linha)
    {
        lock (_gate)
        {
            if (!_porBuild.TryGetValue(buildId, out var linhas)) return;

            linhas.Enqueue(linha);
            while (linhas.Count > MaxLinesPerBuild) linhas.Dequeue();
        }
    }

    /// <summary>
    /// O log de uma build, ou vazio se ela nao rodou nesta sessao — que e o caso
    /// de toda build anterior a ultima vez que o programa abriu.
    /// </summary>
    public IReadOnlyList<string> Linhas(long buildId)
    {
        lock (_gate)
        {
            return _porBuild.TryGetValue(buildId, out var linhas)
                ? linhas.ToArray()
                : Array.Empty<string>();
        }
    }

    /// <summary>Se esta build tem log em memoria, mesmo que ainda sem linhas.</summary>
    public bool Tem(long buildId) => _porBuild.ContainsKey(buildId);

    private void RemoverDaOrdem(long buildId)
    {
        var restantes = _ordem.Where(id => id != buildId).ToArray();
        _ordem.Clear();
        foreach (var id in restantes) _ordem.Enqueue(id);
    }
}
