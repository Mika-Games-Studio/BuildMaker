using UnityLocalCI.Core.Secrets;
using Xunit;

namespace UnityLocalCI.Tests;

public class LinuxCredentialStoreTests : IDisposable
{
    private readonly string _pasta = Path.Combine(
        Path.GetTempPath(), "buildmaker-cofre-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
        GC.SuppressFinalize(this);
    }

    private LinuxCredentialStore Cofre() => new(_pasta);

    [Fact]
    public void Um_segredo_gravado_volta_igual()
    {
        var cofre = Cofre();
        cofre.Write("github-pat", "ghp_exemplo123");

        Assert.Equal("ghp_exemplo123", cofre.Read("github-pat"));
    }

    [Fact]
    public void Uma_credencial_que_nao_existe_devolve_nulo()
    {
        Assert.Null(Cofre().Read("nunca-gravada"));
    }

    [Fact]
    public void Exists_responde_pela_presenca_do_arquivo()
    {
        var cofre = Cofre();
        Assert.False(cofre.Exists("github-pat"));

        cofre.Write("github-pat", "valor");
        Assert.True(cofre.Exists("github-pat"));
    }

    [Fact]
    public void Gravar_de_novo_substitui_o_valor_anterior()
    {
        var cofre = Cofre();
        cofre.Write("token", "primeiro");
        cofre.Write("token", "segundo");

        Assert.Equal("segundo", cofre.Read("token"));
    }

    [Fact]
    public void Um_valor_mais_curto_nao_deixa_resto_do_anterior()
    {
        // WriteAllText trunca, mas e o tipo de coisa que uma mudanca para
        // FileStream em modo Append quebraria em silencio — e o resto do PAT
        // antigo iria junto no cabecalho Authorization.
        var cofre = Cofre();
        cofre.Write("token", "um-valor-bem-longo-aqui");
        cofre.Write("token", "curto");

        Assert.Equal("curto", cofre.Read("token"));
    }

    [Fact]
    public void A_quebra_de_linha_no_fim_do_arquivo_e_descartada()
    {
        // Um PAT com '\n' no fim vira um cabecalho HTTP invalido, e o erro
        // aparece como 401 — que manda procurar no lugar errado.
        var cofre = Cofre();
        Directory.CreateDirectory(_pasta);
        File.WriteAllText(Path.Combine(_pasta, "editado-a-mao"), "ghp_valor\n");

        Assert.Equal("ghp_valor", cofre.Read("editado-a-mao"));
    }

    [Theory]
    [InlineData("../fora")]
    [InlineData("../../etc/passwd")]
    [InlineData("pasta/token")]
    [InlineData(@"pasta\token")]
    public void Um_nome_com_caminho_nao_escapa_da_pasta(string nome)
    {
        var cofre = Cofre();
        cofre.Write(nome, "valor");

        var gravados = Directory.GetFiles(_pasta);
        Assert.Single(gravados);
        Assert.Equal(_pasta, Path.GetDirectoryName(gravados[0]));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void Um_nome_que_e_so_ponto_nao_vira_diretorio(string nome)
    {
        var cofre = Cofre();
        cofre.Write(nome, "valor");

        Assert.Equal("valor", cofre.Read(nome));
    }

    [Fact]
    public void Nomes_diferentes_nao_se_misturam()
    {
        var cofre = Cofre();
        cofre.Write("um", "primeiro");
        cofre.Write("dois", "segundo");

        Assert.Equal("primeiro", cofre.Read("um"));
        Assert.Equal("segundo", cofre.Read("dois"));
    }

    [Fact]
    public void Gravar_sem_nome_e_recusado()
    {
        Assert.Throws<ArgumentException>(() => Cofre().Write("  ", "valor"));
    }

    [Fact]
    public void Gravar_sem_valor_e_recusado()
    {
        Assert.Throws<ArgumentNullException>(() => Cofre().Write("nome", null!));
    }

    [Fact]
    public void Um_valor_vazio_e_valido_e_diferente_de_ausente()
    {
        var cofre = Cofre();
        cofre.Write("vazia", "");

        Assert.Equal("", cofre.Read("vazia"));
        Assert.True(cofre.Exists("vazia"));
    }
}
