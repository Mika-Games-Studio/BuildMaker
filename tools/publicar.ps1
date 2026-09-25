<#
    Gera o pacote distribuivel do UnityLocalCI.

    A publicacao e self-contained e de arquivo unico: o executavel carrega o
    proprio runtime .NET dentro dele. Fica grande — em torno de 150 MB — mas
    resolve de uma vez o problema que mais atrapalha aqui: o .NET instalado no
    perfil de um usuario nao e visto por outra conta, nem pelo Windows Service.
    Quem receber o pacote nao precisa instalar nada.

    Saida: publicado\ com o executavel, e UnityLocalCI.zip para distribuir.
#>
param(
    [string]$Saida  = (Join-Path $PSScriptRoot '..\publicado'),
    [string]$Zip    = (Join-Path $PSScriptRoot '..\UnityLocalCI.zip'),
    [switch]$SemZip
)

$ErrorActionPreference = 'Stop'

$raiz = Resolve-Path (Join-Path $PSScriptRoot '..')
$projeto = Join-Path $raiz 'src\UnityLocalCI.Worker'

function Passo($t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }

Passo "Compilando e testando"
& dotnet test (Join-Path $raiz 'UnityLocalCI.slnx') -c Release --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "Testes falharam. Pacote nao gerado." -ForegroundColor Red; exit 1 }

Passo "Publicando"
if (Test-Path $Saida) { Remove-Item $Saida -Recurse -Force }

& dotnet publish $projeto `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $Saida

if ($LASTEXITCODE -ne 0) { Write-Host "Publicacao falhou." -ForegroundColor Red; exit 1 }

# Os scripts de operacao vao junto: quem recebe o pacote precisa deles para
# registrar o servico, gravar segredos e tocar os gatilhos.
$destinoFerramentas = Join-Path $Saida 'tools'
New-Item -ItemType Directory -Path $destinoFerramentas -Force | Out-Null
foreach ($f in 'install-service.ps1', 'set-secrets.ps1', 'buildar-tudo.ps1', 'buildar-tudo.bat', 'post-merge.hook', 'instalar.ps1') {
    $origem = Join-Path $PSScriptRoot $f
    if (Test-Path $origem) { Copy-Item $origem $destinoFerramentas }
}

Copy-Item (Join-Path $raiz 'README.md') $Saida -ErrorAction SilentlyContinue
Copy-Item (Join-Path $raiz 'TROUBLESHOOTING.md') $Saida -ErrorAction SilentlyContinue

$exe = Join-Path $Saida 'UnityLocalCI.exe'
$tamanho = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "   Executavel: $exe ($tamanho MB)"

# --------------------------------------------------------------- assinatura

# Antes de compactar, para o zip levar o executavel ja assinado.
#
# Sem assinatura, o executavel nao tem quem responda por ele: o Windows o trata
# como programa desconhecido e o antivirus corporativo bloqueia a execucao. Como
# cada build tem hash novo, o bloqueio voltava a cada publicacao.
#
# O certificado e criado por tools\certificado.ps1, que roda uma vez por maquina.
# Aqui ele e so usado — se nao existir, a publicacao continua e avisa: um pacote
# sem assinar e pior que um assinado, mas melhor que nenhum.
Passo "Assinando"

$certificado = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq 'CN=Mika Games Studio, O=Mika Games Studio, C=BR' -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $certificado) {
    Write-Host "   [!]  sem certificado nesta maquina; o pacote sai sem assinatura" -ForegroundColor Yellow
    Write-Host "   [!]  para assinar: powershell -ExecutionPolicy Bypass -File tools\certificado.ps1" -ForegroundColor Yellow
} else {
    # O carimbo de tempo e o que faz a assinatura sobreviver ao vencimento do
    # certificado: sem ele, todo binario ja distribuido passa a ser invalido no
    # dia em que o certificado expira.
    $assinatura = Set-AuthenticodeSignature -FilePath $exe -Certificate $certificado `
        -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' -ErrorAction Continue

    if (-not $assinatura.TimeStamperCertificate) {
        Write-Host "   [!]  sem carimbo de tempo (servidor fora do ar?); a assinatura vence com o certificado" -ForegroundColor Yellow
    }

    switch ($assinatura.Status) {
        'Valid' {
            Write-Host "   assinado por $($certificado.Subject.Split(',')[0].Replace('CN=',''))"
        }
        'UnknownError' {
            # Assinou, mas esta maquina nao confia na raiz. E o estado normal
            # antes de rodar o certificado.ps1 — ou se o dialogo foi recusado.
            Write-Host "   [!]  assinado, mas esta maquina ainda nao confia no certificado" -ForegroundColor Yellow
            Write-Host "   [!]  rode: powershell -ExecutionPolicy Bypass -File tools\certificado.ps1" -ForegroundColor Yellow
        }
        default {
            Write-Host "   [!]  a assinatura ficou como '$($assinatura.Status)'" -ForegroundColor Yellow
            Write-Host "   [!]  $($assinatura.StatusMessage)" -ForegroundColor Yellow
        }
    }
}

if (-not $SemZip) {
    Passo "Compactando"
    if (Test-Path $Zip) { Remove-Item $Zip -Force }

    # ZipFile.CreateFromDirectory, e nao Compress-Archive.
    #
    # O Compress-Archive do Windows PowerShell 5.1 abre cada arquivo com
    # FileShare.None: basta qualquer outro processo ter o arquivo aberto, mesmo
    # so para leitura, e ele falha com IOException/PermissionDenied. Numa maquina
    # com antivirus de tempo real isso acontece direto — os scripts sao copiados
    # para publicado\tools\ poucos segundos antes daqui, e o scanner ainda esta
    # com eles na mao quando a compactacao chega. O sintoma era um pacote que
    # falhava em uma execucao e passava na seguinte, sem nada ter mudado.
    #
    # CreateFromDirectory abre com FileShare.Read, que e o que qualquer leitor
    # deveria usar. De quebra e bem mais rapido para os 52 MB do executavel.
    # As entradas sao montadas uma a uma, e nao com CreateFromDirectory, por
    # causa do separador: o ZipFile do .NET Framework grava 'tools\arquivo', com
    # barra invertida, e a especificacao do zip pede '/'. O Expand-Archive
    # aceita, mas 7-Zip, macOS e Linux criam um arquivo chamado literalmente
    # "tools\instalar.ps1" — e o pacote vai para uma release do GitHub, onde nao
    # se escolhe com o que ele sera aberto.
    # As duas: ZipFile vem da .FileSystem, ZipArchiveMode e CompressionLevel da
    # outra. Carregar so a primeira faz o script morrer no meio da compactacao.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $raizPacote = (Resolve-Path $Saida).Path.TrimEnd('\')
    $arquivos = Get-ChildItem $raizPacote -Recurse -File

    $tentativas = 3
    for ($i = 1; $i -le $tentativas; $i++) {
        try {
            $pacote = [IO.Compression.ZipFile]::Open(
                [IO.Path]::GetFullPath($Zip), [IO.Compression.ZipArchiveMode]::Create)
            try {
                foreach ($arquivo in $arquivos) {
                    $nome = $arquivo.FullName.Substring($raizPacote.Length + 1).Replace('\', '/')
                    $entrada = $pacote.CreateEntry($nome, [IO.Compression.CompressionLevel]::Optimal)

                    # FileShare.Read: e so isto que o Compress-Archive nao faz.
                    $origem = [IO.File]::Open($arquivo.FullName, 'Open', 'Read', 'Read')
                    try {
                        $destino = $entrada.Open()
                        try { $origem.CopyTo($destino) } finally { $destino.Dispose() }
                    } finally { $origem.Dispose() }
                }
            } finally { $pacote.Dispose() }
            break
        } catch [IO.IOException] {
            # Sobra o caso de alguem segurar o arquivo em modo exclusivo, que
            # nenhuma escolha de FileShare resolve. Ai so esperar adianta.
            if ($i -eq $tentativas) { throw }
            Write-Host "   arquivo em uso; tentando de novo ($i de $tentativas)" -ForegroundColor Yellow
            if (Test-Path $Zip) { Remove-Item $Zip -Force }
            Start-Sleep -Seconds 3
        }
    }

    $zipMb = [Math]::Round((Get-Item $Zip).Length / 1MB, 1)
    Write-Host "   $Zip ($zipMb MB)"
}

Write-Host ""
Write-Host "Pronto." -ForegroundColor Green
Write-Host ""
Write-Host "Para instalar nesta maquina:"
Write-Host "    powershell -ExecutionPolicy Bypass -File tools\instalar.ps1" -ForegroundColor Yellow
Write-Host ""
Write-Host "Para distribuir: publique o $([IO.Path]::GetFileName($Zip)) numa release do GitHub."
