<#
    Gera o executavel distribuivel do BuildMaker, assinado.

    A saida e UM arquivo: publicado\UnityLocalCI.exe. Ele e o programa, o
    instalador e o desinstalador ao mesmo tempo — quem recebe abre, e a janela
    oferece o botao Instalar. Nao ha zip para extrair nem script para rodar.

    A publicacao e self-contained e de arquivo unico: o executavel carrega o
    proprio runtime .NET dentro dele. Fica grande — em torno de 50 MB — mas
    resolve de uma vez o problema que mais atrapalha aqui: o .NET instalado no
    perfil de um usuario nao e visto por outra conta, nem pelo Windows Service.
    Quem receber o pacote nao precisa instalar nada.

    ESTE SCRIPT E PARA USO LOCAL

    As releases sao geradas pelo .github\workflows\release.yml, a cada tag: ele
    monta o executavel do Windows e os .deb do Ubuntu na mesma execucao. Aqui e
    onde se gera uma copia para testar antes de marcar a tag.
#>
param(
    [string]$Saida = (Join-Path $PSScriptRoot '..\publicado'),
    [string]$Versao = '1.0.0'
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
    -p:Version=$Versao `
    -o $Saida

if ($LASTEXITCODE -ne 0) { Write-Host "Publicacao falhou." -ForegroundColor Red; exit 1 }

$exe = Join-Path $Saida 'UnityLocalCI.exe'

# Os .pdb e o appsettings.json ao lado sao do build, e nao do que se distribui:
# a configuracao modelo viaja dentro do binario (ver Instalacao) e os simbolos
# so servem para depurar aqui.
Get-ChildItem $Saida -Exclude 'UnityLocalCI.exe' | Remove-Item -Recurse -Force

$tamanho = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "   $exe ($tamanho MB)"

# --------------------------------------------------------------- assinatura

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

Write-Host ""
Write-Host "Pronto." -ForegroundColor Green
Write-Host ""
Write-Host "Para instalar nesta maquina, abra o executavel e clique em Instalar:"
Write-Host "    $exe" -ForegroundColor Yellow
Write-Host ""
Write-Host "Sem janela (automacao):  UnityLocalCI.exe --instalar"
Write-Host "Para desinstalar:        por Configuracoes > Aplicativos, ou --desinstalar"
Write-Host ""
Write-Host "Para publicar uma versao: git tag v$Versao && git push origin v$Versao"
Write-Host "O workflow monta o .exe do Windows e os .deb do Ubuntu na release."
