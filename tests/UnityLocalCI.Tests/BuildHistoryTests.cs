using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.State;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// Apagar do historico, no banco de verdade.
///
/// O que se testa aqui e a recusa: apagar o registro de uma build que ainda
/// esta correndo deixaria o pipeline escrevendo numa linha que nao existe mais,
/// e o sintoma apareceria vinte minutos depois, no fim da build.
/// </summary>
public class BuildHistoryTests : IDisposable
{
    private readonly string _arquivo = Path.Combine(
        Path.GetTempPath(), "unitylocalci-hist-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly SqliteBuildStore _store;

    public BuildHistoryTests()
    {
        _store = new SqliteBuildStore(_arquivo, NullLogger<SqliteBuildStore>.Instance);
        _store.InitializeAsync(default).GetAwaiter().GetResult();
    }

    private async Task<long> Criar(BuildStatus status, string projeto = "Jogo")
        => await _store.CreateAsync(
            new BuildRecord
            {
                Project = projeto,
                Branch = "HML",
                CommitSha = "c81de07c81de07",
                Status = status,
                QueuedAt = DateTimeOffset.UtcNow,
                FinishedAt = status is BuildStatus.Queued or BuildStatus.Running ? null : DateTimeOffset.UtcNow,
            },
            default);

    [Theory]
    [InlineData(BuildStatus.Succeeded)]
    [InlineData(BuildStatus.Failed)]
    [InlineData(BuildStatus.Cancelled)]
    [InlineData(BuildStatus.Interrupted)]
    public async Task Build_que_terminou_sai_do_historico(BuildStatus status)
    {
        var id = await Criar(status);

        Assert.True(await _store.DeleteAsync(id, default));
        Assert.Null(await _store.GetAsync(id, default));
    }

    [Theory]
    [InlineData(BuildStatus.Running)]
    [InlineData(BuildStatus.Queued)]
    public async Task Build_viva_nao_sai(BuildStatus status)
    {
        var id = await Criar(status);

        Assert.False(await _store.DeleteAsync(id, default));
        Assert.NotNull(await _store.GetAsync(id, default));
    }

    [Fact]
    public async Task Apagar_o_que_nao_existe_nao_e_erro()
        => Assert.False(await _store.DeleteAsync(9999, default));

    [Fact]
    public async Task Limpar_leva_as_terminadas_e_deixa_as_vivas()
    {
        await Criar(BuildStatus.Succeeded);
        await Criar(BuildStatus.Failed);
        var correndo = await Criar(BuildStatus.Running);
        var naFila = await Criar(BuildStatus.Queued);

        Assert.Equal(2, await _store.DeleteFinishedAsync(null, default));

        Assert.NotNull(await _store.GetAsync(correndo, default));
        Assert.NotNull(await _store.GetAsync(naFila, default));
    }

    /// <summary>
    /// Limpar o historico de um projeto nao pode levar o do vizinho junto: sao
    /// jogos diferentes, e o time de um nao decide pelo do outro.
    /// </summary>
    [Fact]
    public async Task Limpar_um_projeto_nao_toca_no_outro()
    {
        await Criar(BuildStatus.Succeeded, "Jogo");
        await Criar(BuildStatus.Failed, "Jogo");
        var doOutro = await Criar(BuildStatus.Succeeded, "OutroJogo");

        Assert.Equal(2, await _store.DeleteFinishedAsync("Jogo", default));
        Assert.NotNull(await _store.GetAsync(doOutro, default));
    }

    public void Dispose()
    {
        try { File.Delete(_arquivo); }
        catch (IOException) { /* o SQLite pode ainda estar com o arquivo */ }

        GC.SuppressFinalize(this);
    }
}
