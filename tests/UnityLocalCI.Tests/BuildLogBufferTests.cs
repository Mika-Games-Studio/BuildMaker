using UnityLocalCI.Core.Pipeline;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O log das builds em memoria.
///
/// O que se protege aqui e que ele nao cresca sem fim. O log em disco tinha um
/// limite natural — o disco — e mesmo assim enchia; em memoria nao ha limite
/// natural nenhum, entao os dois tetos (linhas por build, builds guardadas) sao
/// a unica coisa entre uma tarde de builds e um processo de varios gigabytes.
/// </summary>
public class BuildLogBufferTests
{
    private static BuildLogBuffer ComBuild(long id)
    {
        var buffer = new BuildLogBuffer();
        buffer.Comecar(id);
        return buffer;
    }

    [Fact]
    public void As_linhas_saem_na_ordem_em_que_entraram()
    {
        var buffer = ComBuild(1);

        buffer.Escrever(1, "primeira");
        buffer.Escrever(1, "segunda");

        Assert.Equal(["primeira", "segunda"], buffer.Linhas(1));
    }

    [Fact]
    public void Cada_build_tem_o_seu_log()
    {
        var buffer = ComBuild(1);
        buffer.Comecar(2);

        buffer.Escrever(1, "da um");
        buffer.Escrever(2, "da dois");

        Assert.Equal(["da um"], buffer.Linhas(1));
        Assert.Equal(["da dois"], buffer.Linhas(2));
    }

    /// <summary>
    /// Build que nao rodou nesta sessao nao tem log — e o caso de toda build
    /// anterior a ultima vez que o programa abriu.
    /// </summary>
    [Fact]
    public void Build_desconhecida_devolve_vazio()
    {
        var buffer = new BuildLogBuffer();

        Assert.Empty(buffer.Linhas(99));
        Assert.False(buffer.Tem(99));
    }

    [Fact]
    public void Escrever_sem_comecar_nao_guarda_nada()
    {
        var buffer = new BuildLogBuffer();

        buffer.Escrever(7, "linha perdida");

        Assert.Empty(buffer.Linhas(7));
    }

    /// <summary>
    /// Passando do teto, o comeco e descartado. Numa build que falhou, o que
    /// interessa esta no fim.
    /// </summary>
    [Fact]
    public void Passando_do_teto_de_linhas_o_comeco_e_descartado()
    {
        var buffer = ComBuild(1);

        for (var i = 0; i < BuildLogBuffer.MaxLinesPerBuild + 500; i++)
            buffer.Escrever(1, "linha " + i);

        var linhas = buffer.Linhas(1);

        Assert.Equal(BuildLogBuffer.MaxLinesPerBuild, linhas.Count);
        Assert.Equal("linha 500", linhas[0]);
        Assert.Equal("linha " + (BuildLogBuffer.MaxLinesPerBuild + 499), linhas[^1]);
    }

    [Fact]
    public void Passando_do_teto_de_builds_a_mais_antiga_sai()
    {
        var buffer = new BuildLogBuffer();

        for (var id = 1; id <= BuildLogBuffer.MaxBuilds + 1; id++)
        {
            buffer.Comecar(id);
            buffer.Escrever(id, "linha");
        }

        Assert.False(buffer.Tem(1));
        Assert.True(buffer.Tem(2));
        Assert.True(buffer.Tem(BuildLogBuffer.MaxBuilds + 1));
    }

    /// <summary>
    /// Refazer a mesma build recomeca o log dela, e nao acrescenta ao anterior —
    /// senao a tentativa que falhou e a que deu certo virariam um texto so.
    /// </summary>
    [Fact]
    public void Recomecar_a_mesma_build_zera_o_log_dela()
    {
        var buffer = ComBuild(1);
        buffer.Escrever(1, "tentativa antiga");

        buffer.Comecar(1);
        buffer.Escrever(1, "tentativa nova");

        Assert.Equal(["tentativa nova"], buffer.Linhas(1));
    }

    /// <summary>
    /// E recomecar nao pode criar uma segunda posicao na fila de descarte: se
    /// criasse, refazer a mesma build varias vezes expulsaria as outras antes da
    /// hora.
    /// </summary>
    [Fact]
    public void Recomecar_a_mesma_build_nao_expulsa_as_outras_antes_da_hora()
    {
        var buffer = new BuildLogBuffer();
        buffer.Comecar(1);

        for (var i = 0; i < BuildLogBuffer.MaxBuilds; i++) buffer.Comecar(1);
        for (var id = 2; id <= BuildLogBuffer.MaxBuilds; id++) buffer.Comecar(id);

        Assert.True(buffer.Tem(1));
        Assert.True(buffer.Tem(2));
    }
}
