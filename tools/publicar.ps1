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

if (-not $SemZip) {
    Passo "Compactando"
    if (Test-Path $Zip) { Remove-Item $Zip -Force }
    Compress-Archive -Path (Join-Path $Saida '*') -DestinationPath $Zip
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
