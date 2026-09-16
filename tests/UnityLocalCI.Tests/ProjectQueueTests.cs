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
}
