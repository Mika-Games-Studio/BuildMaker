<#
    Instala o UnityLocalCI como servico do Windows.

    Execute como administrador.

    O que ele faz, na ordem da secao 7 da especificacao:
      1. confere .NET, Git e Unity CLI
      2. cria os diretorios de cada projeto e valida escrita em cada destino
      3. lembra de gravar os segredos, se ainda nao existirem
      4. registra o servico com sc.exe
      5. inicia e confirma que subiu
#>
param(
    [string]$Publicado = (Join-Path $PSScriptRoot '..\publicado'),
    [string]$Config,
    [string]$Conta,
    [string]$NomeServico = 'UnityLocalCI',
    [switch]$PularVerificacoes
)

$ErrorActionPreference = 'Stop'

function Passo($texto) { Write-Host ""; Write-Host "== $texto" }
function Ok($texto)    { Write-Host "   [ok]   $texto" }
function Aviso($texto) { Write-Host "   [!]    $texto" }
function Erro($texto)  { Write-Host "   [ERRO] $texto"; exit 1 }

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Erro "Rode este script como administrador."
}

# ---------------------------------------------------------------- 1. requisitos

if (-not $PularVerificacoes) {
    Passo "Requisitos"

    foreach ($ferramenta in 'dotnet', 'git', 'unity') {
        $encontrada = Get-Command $ferramenta -ErrorAction SilentlyContinue
        if ($encontrada) { Ok "$ferramenta em $($encontrada.Source)" }
        else { Erro "$ferramenta nao encontrado no PATH." }
    }

    # A conta de servico nao enxerga o .NET instalado no perfil do usuario.
    $dotnetPath = (Get-Command dotnet).Source
    if ($dotnetPath -like "$env:USERPROFILE*") {
        Aviso "O .NET esta no seu perfil ($dotnetPath)."
        Aviso "Uma conta de servico NAO vai encontra-lo. Instale o .NET para toda a maquina,"
        Aviso "ou defina DOTNET_ROOT no ambiente do servico depois de registra-lo."
    }

    & unity license status 2>&1 | Out-String | Write-Host
}

# ------------------------------------------------------------------ 2. publicar

Passo "Binarios"

if (-not (Test-Path $Publicado)) {
    Erro "Pasta publicada nao encontrada em $Publicado. Rode antes: dotnet publish src\UnityLocalCI.Worker -c Release -o publicado"
}

$exe = Join-Path $Publicado 'UnityLocalCI.Worker.exe'
if (-not (Test-Path $exe)) { Erro "Executavel nao encontrado em $exe." }
Ok "Executavel em $exe"

if (-not $Config) { $Config = Join-Path $Publicado 'appsettings.json' }
if (-not (Test-Path $Config)) { Erro "Configuracao nao encontrada em $Config." }

$json = Get-Content $Config -Raw | ConvertFrom-Json
Ok "Configuracao em $Config"

# ---------------------------------------------------------------- 3. diretorios

Passo "Diretorios"

function Garantir($caminho, $rotulo) {
    if ([string]::IsNullOrWhiteSpace($caminho)) { return }
    if (-not (Test-Path $caminho)) { New-Item -ItemType Directory -Path $caminho -Force | Out-Null }

    # Escrever de verdade: permissao so se descobre tentando.
    $teste = Join-Path $caminho ".unitylocalci-teste"
    try {
        Set-Content -Path $teste -Value 'teste' -Encoding utf8
        Remove-Item $teste -Force
        Ok "$rotulo`: $caminho"
    } catch {
        Aviso "$rotulo`: $caminho SEM PERMISSAO DE ESCRITA"
    }
}

Garantir $json.State.LogFolder 'logs'
Garantir (Split-Path $json.State.DatabasePath -Parent) 'estado'
Garantir $json.Defaults.Publishing.StagingFolder 'staging'

foreach ($p in $json.Projects | Where-Object { $_.Enabled -ne $false }) {
    Garantir (Split-Path $p.Repository.WorkspacePath -Parent) "workspace de $($p.Name)"
    Garantir $p.Publishing.ArtifactFolder "destino de $($p.Name)"
    if ($p.ManualTriggerFile) { Garantir (Split-Path $p.ManualTriggerFile -Parent) "gatilho de $($p.Name)" }
}

if ($json.Scheduler.GlobalStatusFile) {
    Garantir (Split-Path $json.Scheduler.GlobalStatusFile -Parent) 'status geral'
}

# ------------------------------------------------------------------ 4. segredos

Passo "Segredos"

$faltando = @()
foreach ($p in $json.Projects | Where-Object { $_.Enabled -ne $false }) {
    $nome = $p.Repository.PatCredentialName
    if ($nome -and -not (& cmdkey /list:$nome 2>$null | Select-String -SimpleMatch $nome)) {
        $faltando += $nome
    }
}

if ($faltando.Count -gt 0) {
    Aviso "Credenciais ausentes: $($faltando -join ', ')"
    Aviso "O servico recusa subir sem elas. Rode: tools\set-secrets.ps1"
    Aviso "Logado como a conta que vai executar o servico - o Credential Manager e por usuario."
} else {
    Ok "Todas as credenciais referenciadas existem para $env:USERNAME."
}

# ------------------------------------------------------------------- 5. servico

Passo "Servico"

$existente = Get-Service -Name $NomeServico -ErrorAction SilentlyContinue
if ($existente) {
    Aviso "O servico $NomeServico ja existe."
    if ($existente.Status -eq 'Running') {
        Write-Host "   Parando..."
        Stop-Service -Name $NomeServico -Force
    }
    & sc.exe delete $NomeServico | Out-Null
    Start-Sleep -Seconds 2
    Ok "Registro anterior removido."
}

$argumentos = @('create', $NomeServico, "binPath= `"$exe`"", 'start= auto',
                "DisplayName= `"UnityLocalCI`"")
if ($Conta) { $argumentos += "obj= `"$Conta`"" }

& sc.exe @argumentos | Out-Null
if ($LASTEXITCODE -ne 0) { Erro "sc.exe create falhou com codigo $LASTEXITCODE." }

& sc.exe description $NomeServico "CI local para projetos Unity WebGL." | Out-Null

# Reinicio automatico: uma build de 30 minutos nao pode depender de alguem
# perceber que o servico caiu.
& sc.exe failure $NomeServico reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null

Ok "Servico $NomeServico registrado."

if ($Conta) {
    Aviso "Conta $Conta: garanta que ela tem permissao de escrita nos destinos"
    Aviso "e que os segredos foram gravados logado como ela."
}

Passo "Iniciando"

Start-Service -Name $NomeServico
Start-Sleep -Seconds 5

$servico = Get-Service -Name $NomeServico
if ($servico.Status -ne 'Running') {
    Aviso "O servico nao ficou em execucao (status: $($servico.Status))."
    Aviso "Veja o motivo em: $($json.State.LogFolder), ou no Event Log em Application."
    exit 1
}

Ok "Servico em execucao."

Write-Host ""
Write-Host "Pronto. Acompanhe pelo _STATUS-GERAL.txt em:"
Write-Host "  $($json.Scheduler.GlobalStatusFile)"
Write-Host ""
Write-Host "Para parar:     Stop-Service $NomeServico"
Write-Host "Para remover:   sc.exe delete $NomeServico"
