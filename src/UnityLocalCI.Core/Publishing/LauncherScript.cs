using System.Text;

namespace UnityLocalCI.Core.Publishing;

/// <summary>
/// O launcher que vai dentro do zip e da pasta latest\.
///
/// Uma build WebGL nao funciona aberta por file://, porque o navegador bloqueia
/// .wasm e .data nesse protocolo. O sintoma e tela preta sem erro util, e e a
/// duvida numero um de quem recebe o zip. O rodar.bat sobe um servidor estatico
/// na propria pasta e abre o navegador.
///
/// Estrategias, na ordem em que sao tentadas:
///   1. python -m http.server
///   2. npx serve
///   3. PowerShell, que existe em toda maquina Windows
///
/// A especificacao previa, como terceira opcao, um executavel .NET de arquivo
/// unico embutido no zip. Trocamos por PowerShell porque ele ja esta em toda
/// maquina Windows e nao acrescenta dezenas de MB a cada artefato. O servidor em
/// PowerShell ainda tem uma vantagem sobre os outros dois: ele envia
/// Content-Encoding para arquivos .br e .gz, entao roda ate uma build compactada
/// com Brotli, que e justamente o caso que quebraria em qualquer servidor
/// estatico simples.
/// </summary>
public static class LauncherScript
{
    public const string LauncherFileName = "rodar.bat";
    public const string ServerFileName = "_servidor.ps1";

    public const int DefaultPort = 8080;

    /// <summary>Grava o launcher e o servidor auxiliar na pasta indicada.</summary>
    public static async Task WriteAsync(string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);

        // Sem BOM: o interpretador de .bat do Windows engasga com BOM na primeira
        // linha. O chcp 65001 dentro do script cuida dos acentos.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        await File.WriteAllTextAsync(Path.Combine(folder, LauncherFileName), Batch, encoding, ct)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(Path.Combine(folder, ServerFileName), PowerShellServer, encoding, ct)
            .ConfigureAwait(false);
    }

    public const string Batch = """
        @echo off
        chcp 65001 >nul
        setlocal

        set "PORTA=%~1"
        if "%PORTA%"=="" set "PORTA=8080"
        set "PASTA=%~dp0"
        set "URL=http://localhost:%PORTA%/"

        echo.
        echo   UnityLocalCI
        echo   Pasta ...: %PASTA%
        echo   Endereco : %URL%
        echo.
        echo   Se a pagina nao carregar de primeira, atualize com F5:
        echo   o servidor leva um instante para subir.
        echo.
        echo   Feche esta janela para parar o servidor.
        echo.

        where python >nul 2>&1
        if %errorlevel%==0 (
            echo   Servidor: python -m http.server
            start "" "%URL%"
            python -m http.server %PORTA% --directory "%PASTA%"
            goto :fim
        )

        where npx >nul 2>&1
        if %errorlevel%==0 (
            echo   Servidor: npx serve
            start "" "%URL%"
            npx --yes serve --listen %PORTA% "%PASTA%"
            goto :fim
        )

        where powershell >nul 2>&1
        if %errorlevel%==0 (
            echo   Servidor: PowerShell
            start "" "%URL%"
            powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0_servidor.ps1" -Porta %PORTA% -Pasta "%PASTA%"
            goto :fim
        )

        echo   ERRO: nenhum servidor local foi encontrado nesta maquina.
        echo.
        echo   Esta build nao abre por duplo clique no index.html: o navegador
        echo   bloqueia os arquivos .wasm e .data no protocolo file://, e o
        echo   resultado e uma tela preta sem mensagem de erro.
        echo.
        echo   Instale qualquer um destes e rode este arquivo de novo:
        echo     - Python     https://python.org
        echo     - Node.js    https://nodejs.org
        echo.
        pause
        exit /b 1

        :fim
        endlocal

        """;

    public const string PowerShellServer = """
        # Servidor estatico minimo para builds WebGL do Unity.
        # Chamado pelo rodar.bat quando nao ha python nem npx na maquina.
        param(
            [int]$Porta = 8080,
            [string]$Pasta = $PSScriptRoot
        )

        $ErrorActionPreference = 'Stop'

        $tipos = @{
            '.html'     = 'text/html; charset=utf-8'
            '.htm'      = 'text/html; charset=utf-8'
            '.js'       = 'application/javascript'
            '.mjs'      = 'application/javascript'
            '.json'     = 'application/json'
            '.css'      = 'text/css'
            '.wasm'     = 'application/wasm'
            '.data'     = 'application/octet-stream'
            '.unityweb' = 'application/octet-stream'
            '.symbols'  = 'application/octet-stream'
            '.mem'      = 'application/octet-stream'
            '.png'      = 'image/png'
            '.jpg'      = 'image/jpeg'
            '.jpeg'     = 'image/jpeg'
            '.gif'      = 'image/gif'
            '.svg'      = 'image/svg+xml'
            '.ico'      = 'image/x-icon'
            '.txt'      = 'text/plain; charset=utf-8'
        }

        # GetFullPath nos dois lados, e nao Resolve-Path de um e GetFullPath do
        # outro: eles discordam quando o caminho tem nome curto 8.3, e a
        # comparacao de prefixo passaria a recusar tudo com 403. A barra no fim
        # evita que C:\build tambem aceite C:\build-antigo.
        $raiz = [IO.Path]::GetFullPath((Resolve-Path $Pasta).Path)
        if (-not $raiz.EndsWith([IO.Path]::DirectorySeparatorChar)) {
            $raiz = $raiz + [IO.Path]::DirectorySeparatorChar
        }

        $listener = New-Object System.Net.HttpListener
        $listener.Prefixes.Add("http://localhost:$Porta/")

        try {
            $listener.Start()
        } catch {
            Write-Host ""
            Write-Host "  ERRO: nao foi possivel abrir a porta $Porta."
            Write-Host "  Outra coisa ja esta usando essa porta."
            Write-Host "  Rode com outra: rodar.bat 8090"
            Write-Host ""
            exit 1
        }

        Write-Host "  Servindo $raiz"
        Write-Host "  http://localhost:$Porta/"
        Write-Host "  Feche esta janela para parar."

        try {
            while ($listener.IsListening) {
                $contexto = $listener.GetContext()
                $pedido = $contexto.Request
                $resposta = $contexto.Response

                try {
                    $relativo = [Uri]::UnescapeDataString($pedido.Url.AbsolutePath).TrimStart('/')
                    if ([string]::IsNullOrWhiteSpace($relativo)) { $relativo = 'index.html' }

                    $arquivo = Join-Path $raiz ($relativo -replace '/', '\')
                    $completo = [IO.Path]::GetFullPath($arquivo)

                    # Nunca servir nada fora da pasta da build.
                    if (-not $completo.StartsWith($raiz, [StringComparison]::OrdinalIgnoreCase)) {
                        $resposta.StatusCode = 403
                        $resposta.Close()
                        continue
                    }

                    if (-not (Test-Path $completo -PathType Leaf)) {
                        $resposta.StatusCode = 404
                        $resposta.Close()
                        continue
                    }

                    $extensao = [IO.Path]::GetExtension($completo).ToLowerInvariant()

                    # Arquivos .br e .gz continuam comprimidos depois de o zip ser
                    # extraido, e so carregam se o servidor disser como. E por isso
                    # que uma build com Brotli fica em tela preta num servidor
                    # estatico qualquer, e por isso que este aqui trata o caso.
                    if ($extensao -eq '.br' -or $extensao -eq '.gz') {
                        $codificacao = if ($extensao -eq '.br') { 'br' } else { 'gzip' }
                        $resposta.AddHeader('Content-Encoding', $codificacao)
                        $extensao = [IO.Path]::GetExtension([IO.Path]::GetFileNameWithoutExtension($completo)).ToLowerInvariant()
                    }

                    $tipo = $tipos[$extensao]
                    if (-not $tipo) { $tipo = 'application/octet-stream' }
                    $resposta.ContentType = $tipo

                    $bytes = [IO.File]::ReadAllBytes($completo)
                    $resposta.ContentLength64 = $bytes.Length
                    $resposta.OutputStream.Write($bytes, 0, $bytes.Length)
                } catch {
                    $resposta.StatusCode = 500
                } finally {
                    $resposta.Close()
                }
            }
        } finally {
            $listener.Stop()
        }

        """;
}
