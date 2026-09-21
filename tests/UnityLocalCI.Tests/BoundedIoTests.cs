using System.Diagnostics;
using UnityLocalCI.App;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O prazo das leituras de disco feitas pela janela.
///
/// Isto existe por causa de um travamento real: a pasta de destino costuma ser
/// um compartilhamento de rede, e uma maquina desligada nao responde "nao
/// existe" — ela simplesmente nao responde. Sem prazo, a janela esperava o
/// Windows desistir, que leva dezenas de segundos.
/// </summary>
public class BoundedIoTests
{
    [Fact]
    public void Trabalho_rapido_devolve_o_resultado()
    {
        Assert.Equal("pronto", BoundedIo.Run(() => "pronto"));
    }

    [Fact]
    public void Trabalho_lento_e_abandonado_no_prazo()
    {
        var relogio = Stopwatch.StartNew();

        var resultado = BoundedIo.Run(
            () => { Thread.Sleep(TimeSpan.FromSeconds(10)); return "tarde demais"; },
            TimeSpan.FromMilliseconds(300));

        relogio.Stop();

        Assert.Null(resultado);
        Assert.True(relogio.Elapsed < TimeSpan.FromSeconds(3),
            $"a espera deveria terminar no prazo, mas levou {relogio.Elapsed.TotalSeconds:0.0}s");
    }

    /// <summary>Uma excecao la dentro nao pode subir pela pintura de uma tela.</summary>
    [Fact]
    public void Excecao_vira_valor_padrao()
    {
        Assert.Null(BoundedIo.Run<string>(() => throw new IOException("disco sumiu")));
    }

    /// <summary>
    /// O caso que travava a janela de verdade.
    ///
    /// Nome que nao resolve falha na hora. O que prende e um endereco que aceita
    /// a pergunta e nao responde — maquina desligada, pacote descartado. Medido
    /// uma vez nesta maquina, um Directory.Exists cru nesse endereco levou 11,2
    /// segundos; depois disso o Windows passa a recusar rapido, entao este teste
    /// sozinho nao prova o prazo. Quem prova e o do trabalho lento, acima. Este
    /// garante que o caminho inteiro do editor respeita o teto.
    /// </summary>
    [Fact]
    public void Caminho_de_rede_que_nao_responde_nao_segura_a_janela()
    {
        var relogio = Stopwatch.StartNew();

        var resultado = FolderPathEditor.PrimeiraPastaExistente(@"\\10.255.255.1\builds\projeto\destino");

        relogio.Stop();

        Assert.Null(resultado);
        Assert.True(relogio.Elapsed < TimeSpan.FromSeconds(5),
            $"a sondagem deveria respeitar o prazo, mas levou {relogio.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public void Caminho_local_inexistente_devolve_a_pasta_mais_proxima()
    {
        var temporaria = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        var achada = FolderPathEditor.PrimeiraPastaExistente(
            Path.Combine(temporaria, "nao-existe-" + Guid.NewGuid().ToString("N"), "nem-isto"));

        Assert.Equal(temporaria, achada?.TrimEnd(Path.DirectorySeparatorChar));
    }
}
