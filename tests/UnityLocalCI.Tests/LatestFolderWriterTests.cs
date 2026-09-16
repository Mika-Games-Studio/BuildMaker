using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Publishing;
using Xunit;

namespace UnityLocalCI.Tests;

public class LatestFolderWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "unitylocalci-tests", Guid.NewGuid().ToString("N"));

    private readonly string _destination;
    private readonly string _source;

    public LatestFolderWriterTests()
    {
        _destination = Path.Combine(_root, "destino");
        _source = Path.Combine(_root, "build");
        Directory.CreateDirectory(_destination);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* limpeza best effort */ }
    }

    private static LatestFolderWriter Create() => new(NullLogger<LatestFolderWriter>.Instance);

    private string Latest => Path.Combine(_destination, "latest");

    private void SeedSource(string marker)
    {
        if (Directory.Exists(_source)) Directory.Delete(_source, recursive: true);

        Directory.CreateDirectory(Path.Combine(_source, "Build"));
        File.WriteAllText(Path.Combine(_source, "index.html"), marker);
        File.WriteAllText(Path.Combine(_source, "rodar.bat"), "@echo off");
        File.WriteAllText(Path.Combine(_source, "Build", "jogo.wasm"), marker);
    }

    [Fact]
    public async Task Cria_a_pasta_latest_com_a_build_inteira()
    {
        SeedSource("build-1");

        var error = await Create().UpdateAsync(_destination, _source, default);

        Assert.Null(error);
        Assert.Equal("build-1", File.ReadAllText(Path.Combine(Latest, "index.html")));
        Assert.True(File.Exists(Path.Combine(Latest, "rodar.bat")));
        // Subpastas tambem: uma latest\ sem Build\ e uma tela preta garantida.
        Assert.Equal("build-1", File.ReadAllText(Path.Combine(Latest, "Build", "jogo.wasm")));
    }

    [Fact]
    public async Task Substitui_a_build_anterior()
    {
        SeedSource("build-1");
        await Create().UpdateAsync(_destination, _source, default);

        SeedSource("build-2");
        await Create().UpdateAsync(_destination, _source, default);

        Assert.Equal("build-2", File.ReadAllText(Path.Combine(Latest, "index.html")));
    }

    [Fact]
    public async Task Arquivo_que_sumiu_entre_builds_nao_sobrevive_na_latest()
    {
        SeedSource("build-1");
        File.WriteAllText(Path.Combine(_source, "sobra.txt"), "removido na proxima");
        await Create().UpdateAsync(_destination, _source, default);
        Assert.True(File.Exists(Path.Combine(Latest, "sobra.txt")));

        // A troca e por rename, nao por copia por cima: o que saiu da build sai
        // da pasta tambem, em vez de acumular lixo de builds antigas.
        SeedSource("build-2");
        await Create().UpdateAsync(_destination, _source, default);

        Assert.False(File.Exists(Path.Combine(Latest, "sobra.txt")));
    }

    [Fact]
    public async Task Nao_deixa_pastas_intermediarias_para_tras()
    {
        SeedSource("build-1");
        await Create().UpdateAsync(_destination, _source, default);
        SeedSource("build-2");
        await Create().UpdateAsync(_destination, _source, default);

        Assert.False(Directory.Exists(Path.Combine(_destination, "latest.new")));
        Assert.False(Directory.Exists(Path.Combine(_destination, "latest.old")));
    }

    [Fact]
    public async Task Build_ausente_preserva_a_latest_anterior()
    {
        SeedSource("build-1");
        await Create().UpdateAsync(_destination, _source, default);

        Directory.Delete(_source, recursive: true);
        var error = await Create().UpdateAsync(_destination, _source, default);

        Assert.NotNull(error);
        // Melhor a build anterior inteira do que a nova pela metade.
        Assert.Equal("build-1", File.ReadAllText(Path.Combine(Latest, "index.html")));
    }

    [Fact]
    public async Task Troca_interrompida_devolve_a_versao_anterior_ao_lugar()
    {
        // Simula o processo morrendo entre os dois renames: latest\ nao existe e
        // latest.old\ sim. Sem recuperacao, o time ficaria sem pasta nenhuma.
        var previous = Path.Combine(_destination, "latest.old");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "index.html"), "build-anterior");

        SeedSource("build-nova");
        var error = await Create().UpdateAsync(_destination, _source, default);

        Assert.Null(error);
        Assert.Equal("build-nova", File.ReadAllText(Path.Combine(Latest, "index.html")));
        Assert.False(Directory.Exists(previous));
    }
}

public class LauncherScriptTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "unitylocalci-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* limpeza best effort */ }
    }

    [Fact]
    public async Task Grava_o_launcher_e_o_servidor_auxiliar()
    {
        await LauncherScript.WriteAsync(_folder, default);

        Assert.True(File.Exists(Path.Combine(_folder, "rodar.bat")));
        Assert.True(File.Exists(Path.Combine(_folder, "_servidor.ps1")));
    }

    [Fact]
    public async Task Launcher_nao_tem_bom_que_quebraria_o_interpretador_de_bat()
    {
        await LauncherScript.WriteAsync(_folder, default);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_folder, "rodar.bat"));

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal((byte)'@', bytes[0]);
    }

    [Fact]
    public void Launcher_tenta_as_tres_estrategias_na_ordem()
    {
        var python = LauncherScript.Batch.IndexOf("python -m http.server", StringComparison.Ordinal);
        var npx = LauncherScript.Batch.IndexOf("npx --yes serve", StringComparison.Ordinal);
        var powershell = LauncherScript.Batch.IndexOf("_servidor.ps1", StringComparison.Ordinal);

        Assert.True(python > 0 && npx > python && powershell > npx,
            "as estrategias precisam aparecer em ordem de preferencia");
    }

    [Fact]
    public void Launcher_explica_o_motivo_quando_nao_ha_servidor()
    {
        // Falhar em silencio aqui deixaria a pessoa com uma tela preta e nenhuma
        // pista de por que o duplo clique no index.html nao funciona.
        Assert.Contains("ERRO: nenhum servidor local", LauncherScript.Batch, StringComparison.Ordinal);
        Assert.Contains("file://", LauncherScript.Batch, StringComparison.Ordinal);
        Assert.Contains("python.org", LauncherScript.Batch, StringComparison.Ordinal);
        Assert.Contains("nodejs.org", LauncherScript.Batch, StringComparison.Ordinal);
    }

    [Fact]
    public void Servidor_serve_wasm_com_o_tipo_certo_e_trata_brotli()
    {
        Assert.Contains("'.wasm'     = 'application/wasm'", LauncherScript.PowerShellServer, StringComparison.Ordinal);

        // O tratamento de Content-Encoding e o que faz uma build com Brotli
        // carregar aqui, sendo que quebraria em qualquer servidor estatico comum.
        Assert.Contains("Content-Encoding", LauncherScript.PowerShellServer, StringComparison.Ordinal);
        Assert.Contains("'.br'", LauncherScript.PowerShellServer, StringComparison.Ordinal);
    }

    [Fact]
    public void Servidor_nao_serve_nada_fora_da_pasta_da_build()
    {
        Assert.Contains("StartsWith($raiz", LauncherScript.PowerShellServer, StringComparison.Ordinal);
        Assert.Contains("403", LauncherScript.PowerShellServer, StringComparison.Ordinal);
    }
}
