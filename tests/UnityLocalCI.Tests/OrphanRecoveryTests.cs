using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.State;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O fechamento dos registros que ficaram abertos quando o processo parou.
///
/// O que se testa aqui e que nao sobra registro aberto. Um Running ou um Queued
/// que atravessa a reinicializacao nunca mais se fecha sozinho — e, como build
/// viva nao pode ser apagada do historico, ele fica na lista para sempre. Foi
/// exatamente o que aconteceu: quatro linhas presas em Queued desde a antevespera.
/// </summary>
public class OrphanRecoveryTests
{
    private readonly InMemoryBuildStore _store = new();

    private OrphanRecovery Create()
        => new(_store, new FakeClock(), NullLogger<OrphanRecovery>.Instance);

    private async Task<long> SemearAsync(BuildStatus status, string project = "Crash")
        => await _store.CreateAsync(new BuildRecord
        {
            CommitSha = new string('a', 40),
            Project = project,
            Branch = "HML",
            Status = status,
            QueuedAt = DateTimeOffset.UnixEpoch,
            StartedAt = status == BuildStatus.Running ? DateTimeOffset.UnixEpoch : null,
        }, default);

    [Fact]
    public async Task Build_que_estava_correndo_vira_interrompida()
    {
        var id = await SemearAsync(BuildStatus.Running);

        await Create().RecoverAsync([TestProjects.Create()], default);

        var registro = await _store.GetAsync(id, default);
        Assert.Equal(BuildStatus.Interrupted, registro!.Status);
        Assert.NotNull(registro.FinishedAt);
    }

    /// <summary>
    /// A que esperava na fila nao chegou a comecar, entao ela e cancelada e nao
    /// interrompida. A palavra muda o que quem le entende do que aconteceu.
    /// </summary>
    [Fact]
    public async Task Build_que_esperava_na_fila_vira_cancelada()
    {
        var id = await SemearAsync(BuildStatus.Queued);

        await Create().RecoverAsync([TestProjects.Create()], default);

        var registro = await _store.GetAsync(id, default);
        Assert.Equal(BuildStatus.Cancelled, registro!.Status);
        Assert.NotNull(registro.FinishedAt);
    }

    /// <summary>
    /// O ponto de tudo: depois da inicializacao nao pode restar registro aberto,
    /// nem de projeto que saiu da configuracao. Registro aberto e registro que a
    /// lixeira se recusa a apagar.
    /// </summary>
    [Fact]
    public async Task Nao_sobra_registro_aberto_nem_de_projeto_desabilitado()
    {
        await SemearAsync(BuildStatus.Running);
        await SemearAsync(BuildStatus.Queued);
        await SemearAsync(BuildStatus.Running, "ProjetoQueSaiuDaConfiguracao");
        await SemearAsync(BuildStatus.Queued, "ProjetoQueSaiuDaConfiguracao");

        await Create().RecoverAsync([TestProjects.Create()], default);

        Assert.Empty(await _store.GetByStatusAsync(BuildStatus.Running, default));
        Assert.Empty(await _store.GetByStatusAsync(BuildStatus.Queued, default));
    }

    /// <summary>
    /// A recuperacao nao refaz build nenhuma. Ela reenfileirava o commit quando
    /// ele ainda era o HEAD, e isso virava um ciclo: sobe, comeca a mesma build
    /// de quinze minutos, cai no meio, sobe de novo.
    /// </summary>
    [Fact]
    public async Task Nada_volta_para_a_fila()
    {
        await SemearAsync(BuildStatus.Running);

        await Create().RecoverAsync([TestProjects.Create()], default);

        Assert.Empty(await _store.GetByStatusAsync(BuildStatus.Queued, default));
    }

    [Fact]
    public async Task Build_que_ja_tinha_terminado_nao_e_mexida()
    {
        var id = await SemearAsync(BuildStatus.Succeeded);

        await Create().RecoverAsync([TestProjects.Create()], default);

        Assert.Equal(BuildStatus.Succeeded, (await _store.GetAsync(id, default))!.Status);
    }

    [Fact]
    public async Task Sem_registro_aberto_nada_acontece()
        => await Create().RecoverAsync([TestProjects.Create()], default);
}
