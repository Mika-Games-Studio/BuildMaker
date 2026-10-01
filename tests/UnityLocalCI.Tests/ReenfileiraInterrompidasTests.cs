using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.State;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O commit cuja build foi interrompida volta para a fila — e para de voltar
/// depois do teto.
///
/// O problema que isto resolve: o last_built_sha e gravado no momento do
/// enfileiramento, para o mesmo commit nunca ser construido duas vezes. Quando o
/// processo morre no meio da build, o commit fica marcado como construido para
/// sempre, e o merge some sem que ninguem perceba. Aconteceu em uso: um merge
/// ficou tres dias marcado como feito, sem artefato nenhum.
///
/// O teto existe porque a versao anterior disto era incondicional e virava
/// ciclo: sobe, comeca a mesma build de quinze minutos, cai, sobe.
/// </summary>
public class ReenfileiraInterrompidasTests
{
    private const string Projeto = "HumanXRobots";
    private static readonly string Sha = new('b', 40);
    private static readonly string OutroSha = new('c', 40);

    private readonly InMemoryBuildStore _store = new();

    private OrphanRecovery Create(int teto = 1)
        => new(
            _store,
            Options.Create(new CiOptions
            {
                Scheduler = new SchedulerOptions { MaxInterruptedRetries = teto },
            }),
            new FakeClock(),
            NullLogger<OrphanRecovery>.Instance);

    /// <summary>Uma build que ficou presa em Running porque o processo morreu.</summary>
    private async Task<long> SemearOrfaAsync(string sha)
    {
        var id = await _store.CreateAsync(new BuildRecord
        {
            CommitSha = sha,
            Project = Projeto,
            Branch = "main",
            Status = BuildStatus.Queued,
            QueuedAt = DateTimeOffset.UnixEpoch,
        }, default);

        var registro = await _store.GetAsync(id, default);
        await _store.UpdateAsync(registro! with
        {
            Status = BuildStatus.Running,
            StartedAt = DateTimeOffset.UnixEpoch,
        }, default);

        // O enfileiramento grava o marcador: e essa gravacao que prende o commit.
        await _store.SetWatcherValueAsync(Projeto, WatcherFields.LastBuiltSha, sha, default);

        return id;
    }

    private async Task<long> SemearTerminadaAsync(string sha, BuildStatus status)
    {
        var id = await _store.CreateAsync(new BuildRecord
        {
            CommitSha = sha,
            Project = Projeto,
            Branch = "main",
            Status = BuildStatus.Queued,
            QueuedAt = DateTimeOffset.UnixEpoch,
        }, default);

        var registro = await _store.GetAsync(id, default);
        await _store.UpdateAsync(registro! with
        {
            Status = status,
            StartedAt = DateTimeOffset.UnixEpoch,
            FinishedAt = DateTimeOffset.UnixEpoch.AddMinutes(5),
        }, default);

        await _store.SetWatcherValueAsync(Projeto, WatcherFields.LastBuiltSha, sha, default);

        return id;
    }

    private Task RecuperarAsync(int teto = 1)
        => Create(teto).RecoverAsync([TestProjects.Create(Projeto)], default);

    private Task<string?> MarcadorAsync()
        => _store.GetWatcherValueAsync(Projeto, WatcherFields.LastBuiltSha, default);

    // ------------------------------------------------------------- o caso base

    [Fact]
    public async Task O_commit_interrompido_deixa_de_estar_marcado_como_construido()
    {
        await SemearOrfaAsync(Sha);

        await RecuperarAsync();

        // Sem marcador, o watcher enxerga o commit de novo no proximo ciclo.
        Assert.Null(await MarcadorAsync());
    }

    [Fact]
    public async Task O_registro_continua_fechado_como_interrompido()
    {
        // Reenfileirar nao apaga o que aconteceu: a tentativa perdida fica no
        // historico, senao o teto nao teria como ser contado.
        var id = await SemearOrfaAsync(Sha);

        await RecuperarAsync();

        var registro = await _store.GetAsync(id, default);
        Assert.Equal(BuildStatus.Interrupted, registro!.Status);
    }

    // ------------------------------------------------------------------- o teto

    [Fact]
    public async Task Depois_do_teto_o_commit_para_de_voltar()
    {
        // Uma interrupcao anterior do mesmo commit ja esta no historico; esta e
        // a segunda. Com teto 1, a segunda e a ultima.
        await SemearTerminadaAsync(Sha, BuildStatus.Interrupted);
        await SemearOrfaAsync(Sha);

        await RecuperarAsync(teto: 1);

        Assert.Equal(Sha, await MarcadorAsync());
    }

    [Fact]
    public async Task Com_teto_maior_o_commit_ainda_volta()
    {
        await SemearTerminadaAsync(Sha, BuildStatus.Interrupted);
        await SemearOrfaAsync(Sha);

        await RecuperarAsync(teto: 2);

        Assert.Null(await MarcadorAsync());
    }

    [Fact]
    public async Task Teto_zero_devolve_o_comportamento_anterior()
    {
        await SemearOrfaAsync(Sha);

        await RecuperarAsync(teto: 0);

        Assert.Equal(Sha, await MarcadorAsync());
    }

    // --------------------------------------------- o que NAO deve ser refeito

    [Fact]
    public async Task Uma_build_que_falhou_nao_volta()
    {
        // Falhou e um veredito sobre aquele commit. Refazer daria o mesmo
        // veredito, e o ciclo se formaria a cada reinicio.
        await SemearTerminadaAsync(Sha, BuildStatus.Failed);

        await RecuperarAsync();

        Assert.Equal(Sha, await MarcadorAsync());
    }

    [Fact]
    public async Task Uma_build_cancelada_na_janela_nao_volta()
    {
        // Cancelar e alguem mandando parar. Voltar sozinha seria desobedecer.
        await SemearTerminadaAsync(Sha, BuildStatus.Cancelled);

        await RecuperarAsync();

        Assert.Equal(Sha, await MarcadorAsync());
    }

    [Fact]
    public async Task Uma_build_que_passou_nao_volta()
    {
        await SemearTerminadaAsync(Sha, BuildStatus.Succeeded);

        await RecuperarAsync();

        Assert.Equal(Sha, await MarcadorAsync());
    }

    [Fact]
    public async Task So_a_build_mais_recente_decide()
    {
        // Interrompida antes, refeita depois e concluida: aquele commit ja teve
        // seu veredito, e a interrupcao antiga nao pode ressuscita-lo.
        await SemearTerminadaAsync(Sha, BuildStatus.Interrupted);
        await SemearTerminadaAsync(Sha, BuildStatus.Succeeded);

        await RecuperarAsync();

        Assert.Equal(Sha, await MarcadorAsync());
    }

    [Fact]
    public async Task Um_commit_que_ja_tem_build_bem_sucedida_nao_volta()
    {
        // O artefato daquele commit existe. Acontece quando alguem manda
        // 'Construir agora' num commit ja construido e e ESSA build que morre:
        // a mais recente e uma interrupcao, mas nada se perdeu.
        //
        // Este caso apareceu ao rodar a recuperacao contra um banco de verdade,
        // e nao no papel.
        await SemearTerminadaAsync(Sha, BuildStatus.Succeeded);
        await SemearOrfaAsync(Sha);

        await RecuperarAsync();

        Assert.Equal(Sha, await MarcadorAsync());
    }

    [Fact]
    public async Task Uma_build_bem_sucedida_de_OUTRO_commit_nao_impede_a_retentativa()
    {
        // O que protege e ter passado NAQUELE commit. O sucesso do anterior nao
        // diz nada sobre o que acabou de ser interrompido.
        await SemearTerminadaAsync(OutroSha, BuildStatus.Succeeded);
        await SemearOrfaAsync(Sha);

        await RecuperarAsync();

        Assert.Null(await MarcadorAsync());
    }

    [Fact]
    public async Task Se_o_marcador_ja_aponta_para_outro_commit_nada_e_mexido()
    {
        // O repositorio andou enquanto o servico estava fora. O watcher vai
        // pegar o commit novo sozinho; apagar o marcador aqui nao ajudaria e
        // ainda faria perder de onde ele estava.
        await SemearOrfaAsync(Sha);
        await _store.SetWatcherValueAsync(Projeto, WatcherFields.LastBuiltSha, OutroSha, default);

        await RecuperarAsync();

        Assert.Equal(OutroSha, await MarcadorAsync());
    }

    [Fact]
    public async Task A_interrupcao_de_um_projeto_nao_mexe_no_marcador_de_outro()
    {
        await SemearOrfaAsync(Sha);
        await _store.SetWatcherValueAsync("OutroJogo", WatcherFields.LastBuiltSha, OutroSha, default);

        await RecuperarAsync();

        Assert.Equal(OutroSha, await _store.GetWatcherValueAsync("OutroJogo", WatcherFields.LastBuiltSha, default));
    }

    [Fact]
    public async Task Sem_build_nenhuma_a_recuperacao_nao_faz_nada()
    {
        await RecuperarAsync();

        Assert.Null(await MarcadorAsync());
    }
}
