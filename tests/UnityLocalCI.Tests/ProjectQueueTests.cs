using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Queue;
using Xunit;

namespace UnityLocalCI.Tests;

public class ProjectQueueTests
{
    private static BuildJob Job(long id, char sha) => new()
    {
        BuildId = id,
        Project = TestProjects.Create(),
        Commit = new CommitInfo(new string(sha, 40), "Fulano", "mensagem"),
        Trigger = BuildTrigger.Poll,
        QueuedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Job_novo_substitui_o_enfileirado()
    {
        var queue = new ProjectQueue("Crash");

        Assert.Null(queue.Enqueue(Job(1, 'a')));

        var replaced = queue.Enqueue(Job(2, 'b'));

        Assert.NotNull(replaced);
        Assert.Equal(1, replaced!.BuildId);
        Assert.Equal(2, queue.Peek()!.BuildId);
    }

    [Fact]
    public void Job_em_execucao_nunca_e_substituido()
    {
        var queue = new ProjectQueue("Crash");
        queue.Enqueue(Job(1, 'a'));

        // Take simula o consumidor comecando a build: o job saiu da fila.
        var running = queue.Take();
        Assert.Equal(1, running!.BuildId);

        // O que chega agora ocupa a fila vazia, sem substituir nada.
        var replaced = queue.Enqueue(Job(2, 'b'));

        Assert.Null(replaced);
        Assert.Equal(2, queue.Peek()!.BuildId);
    }

    [Fact]
    public void Take_devolve_sempre_o_mais_recente()
    {
        var queue = new ProjectQueue("Crash");

        queue.Enqueue(Job(1, 'a'));
        queue.Enqueue(Job(2, 'b'));
        queue.Enqueue(Job(3, 'c'));

        Assert.Equal(3, queue.Take()!.BuildId);
        Assert.Null(queue.Take());
    }

    [Fact]
    public async Task Sinal_e_emitido_uma_vez_por_fila_vazia()
    {
        var queue = new ProjectQueue("Crash");

        queue.Enqueue(Job(1, 'a'));
        queue.Enqueue(Job(2, 'b'));

        await queue.WaitForPendingAsync(CancellationToken.None);

        // Substituir nao acrescenta trabalho novo: nao pode haver um segundo sinal
        // pendente, senao o consumidor giraria em falso com a fila vazia.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.WaitForPendingAsync(cts.Token));
    }
[Fact]
    public void Tirar_da_fila_o_job_que_ainda_nao_comecou()
    {
        var queue = new ProjectQueue("Crash");
        queue.Enqueue(Job(1, 'a'));

        var removido = queue.Remove(1);

        Assert.NotNull(removido);
        Assert.Equal(1, removido!.BuildId);
        Assert.False(queue.HasPending);
        Assert.Null(queue.Take());
    }

    /// <summary>
    /// A corrida normal: entre o clique na lixeira e o Remove, o consumidor
    /// retirou o job para executar. Aqui nao ha mais o que tirar da fila, e
    /// quem chama tenta o cancelamento da build em execucao.
    /// </summary>
    [Fact]
    public void Job_que_ja_saiu_para_executar_nao_e_encontrado_na_fila()
    {
        var queue = new ProjectQueue("Crash");
        queue.Enqueue(Job(1, 'a'));
        queue.Take();

        Assert.Null(queue.Remove(1));
    }

    [Fact]
    public void Tirar_da_fila_nao_toca_num_job_de_outro_id()
    {
        var queue = new ProjectQueue("Crash");
        queue.Enqueue(Job(7, 'a'));

        Assert.Null(queue.Remove(8));
        Assert.Equal(7, queue.Peek()!.BuildId);
    }

    /// <summary>
    /// Depois de esvaziar a fila pela lixeira, um commit novo tem de voltar a
    /// acordar o consumidor. Sem isso o projeto ficaria parado para sempre.
    /// </summary>
    [Fact]
    public async Task Depois_de_tirar_da_fila_um_job_novo_ainda_sinaliza()
    {
        var queue = new ProjectQueue("Crash");
        queue.Enqueue(Job(1, 'a'));
        queue.Remove(1);

        queue.Enqueue(Job(2, 'b'));

        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.WaitForPendingAsync(prazo.Token);

        Assert.Equal(2, queue.Take()!.BuildId);
    }
}
