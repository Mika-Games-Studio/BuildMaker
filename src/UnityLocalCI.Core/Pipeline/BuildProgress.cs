using System.Collections.Concurrent;

namespace UnityLocalCI.Core.Pipeline;

/// <summary>
/// Em que etapa cada build em execucao esta, agora.
///
/// Existe so para a janela ter o que mostrar enquanto a build corre. "EM
/// EXECUCAO" sem mais nada e igual em dois minutos e em vinte, e a pergunta de
/// quem olha — travou? — nao tem resposta na tela; com a etapa, tem.
///
/// Nao e estado da build: nada aqui e persistido, e reiniciar o servico
/// esvazia o mapa. O que aconteceu de verdade continua no banco e no log, que
/// sao a fonte. Este e um letreiro, e um letreiro pode apagar.
/// </summary>
public sealed class BuildProgress
{
    private readonly ConcurrentDictionary<long, string> _etapas = new();

    /// <summary>A build entrou nesta etapa.</summary>
    public void Entrou(long buildId, string etapa) => _etapas[buildId] = etapa;

    /// <summary>A build terminou, de qualquer jeito. Sai do mapa.</summary>
    public void Saiu(long buildId) => _etapas.TryRemove(buildId, out _);

    /// <summary>
    /// A etapa em curso, ou nulo quando a build nao comecou nenhuma — o que
    /// acontece entre entrar na fila e a primeira etapa, e depois de terminar.
    /// </summary>
    public string? Etapa(long buildId) => _etapas.TryGetValue(buildId, out var etapa) ? etapa : null;
}
