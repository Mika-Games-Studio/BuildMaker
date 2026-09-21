using UnityLocalCI.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Git;
using Xunit;

namespace UnityLocalCI.Tests;

public class GitClientTests
{
    private static GitContext Context(string? pat = null) => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "unitylocalci-tests", "ws"),
        RepositoryUrl = "https://example.invalid/repo",
        Branch = "HML",
        PersonalAccessToken = pat,
    };

    private static GitClient Create(RecordingProcessRunner runner)
        => new(runner, NullLogger<GitClient>.Instance);

    [Fact]
    public async Task Checkout_invoca_o_clean_com_as_exclusoes()
    {
        var runner = new RecordingProcessRunner();
        var client = Create(runner);

        await client.CheckoutAsync(Context(), new string('a', 40), CancellationToken.None);

        var clean = runner.Requests.Single(r => r.Arguments.Contains("clean"));
        foreach (var preserved in GitCleanArguments.PreservedPaths)
            Assert.Contains(preserved, clean.Arguments);
    }

    [Fact]
    public async Task Pat_vai_por_extraheader_e_nunca_na_url_do_remote()
    {
        var runner = new RecordingProcessRunner();
        var client = Create(runner);
        const string pat = "segredo-do-pat";

        await client.FetchAsync(Context(pat), CancellationToken.None);

        var request = runner.Requests.Single();

        Assert.Contains(request.Arguments, a => a.StartsWith("http.extraHeader=Authorization: Basic", StringComparison.Ordinal));
        Assert.DoesNotContain(request.Arguments, a => a.Contains(pat, StringComparison.Ordinal));
        Assert.DoesNotContain(request.Arguments, a => a.Contains("@example.invalid", StringComparison.Ordinal));
    }

    /// <summary>
    /// O usuario do Basic nao pode ser vazio.
    ///
    /// Isto custou uma investigacao: com ':PAT', o GitHub recusa a
    /// autenticacao, o git cai no gerenciador de credenciais e morre com
    /// "Cannot prompt because user interactivity has been disabled" — mensagem
    /// que fala de prompt, quando o problema e o formato do cabecalho. Medido
    /// contra um repositorio real: com usuario vazio falha, com usuario
    /// preenchido autentica.
    /// </summary>
    [Fact]
    public async Task O_usuario_do_basic_nunca_vai_vazio()
    {
        var runner = new RecordingProcessRunner();
        const string pat = "segredo-do-pat";

        await Create(runner).FetchAsync(Context(pat), CancellationToken.None);

        var header = runner.Requests.Single().Arguments
            .Single(a => a.StartsWith("http.extraHeader=Authorization: Basic", StringComparison.Ordinal));

        var base64 = header["http.extraHeader=Authorization: Basic ".Length..];
        var decodificado = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));

        Assert.Equal("x-access-token:" + pat, decodificado);
        Assert.False(decodificado.StartsWith(':'), "usuario vazio: o GitHub recusa");
    }

    [Fact]
    public async Task Linha_de_comando_logada_nao_contem_o_segredo()
    {
        var runner = new RecordingProcessRunner();
        var client = Create(runner);
        const string pat = "segredo-do-pat";

        await client.FetchAsync(Context(pat), CancellationToken.None);

        var safeCommandLine = runner.Requests.Single().SafeCommandLine;

        Assert.DoesNotContain(pat, safeCommandLine, StringComparison.Ordinal);
        // Tambem nao pode vazar em base64, que e como o header carrega o PAT.
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"x-access-token:{pat}"));
        Assert.DoesNotContain(encoded, safeCommandLine, StringComparison.Ordinal);
        Assert.Contains("<REDACTED>", safeCommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Falha_do_git_vira_excecao_tipada_com_stderr_redigido()
    {
        const string pat = "segredo-do-pat";
        var runner = new RecordingProcessRunner
        {
            Respond = _ => new ProcessResult(128, false, "", $"fatal: Authentication failed for {pat}"),
        };
        var client = Create(runner);

        var exception = await Assert.ThrowsAsync<GitCommandException>(
            () => client.FetchAsync(Context(pat), CancellationToken.None));

        Assert.Equal(128, exception.ExitCode);
        Assert.DoesNotContain(pat, exception.StandardError, StringComparison.Ordinal);
        Assert.Contains("<REDACTED>", exception.StandardError, StringComparison.Ordinal);
    }
}
