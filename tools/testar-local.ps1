<#
    Prepara um ambiente de teste descartavel para ver a ferramenta funcionando
    sem tocar em nenhum projeto Unity real.

    Ele cria:
      - um repositorio git local, com um commit, que faz o papel do repositorio
        de homologacao
      - as pastas de workspace, staging, destino, logs, estado e gatilhos
      - um appsettings.local.json apontando para tudo isso

    A build vai FALHAR, e isso e o esperado: a pasta clonada nao e um projeto
    Unity. O que da para conferir e todo o resto — watcher, debounce, fila,
    gatilho manual, _STATUS.txt, _HISTORICO.txt, _STATUS-GERAL.txt, copia do
    log, sinal em loopback e a janela inteira.

    Para voltar ao normal, apague o appsettings.local.json da pasta de saida.
#>
param(
    [string]$Raiz = 'C:\ci-teste',
    [string]$Saida = (Join-Path $PSScriptRoot '..\src\UnityLocalCI.Worker\bin\Debug\net10.0-windows')
)

$ErrorActionPreference = 'Stop'

function Passo($t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }
function Ok($t)    { Write-Host "   [ok] $t" }

if (-not (Test-Path $Saida)) {
    Write-Host "Pasta de saida nao encontrada: $Saida"
    Write-Host "Compile antes:  dotnet build"
    exit 1
}
$Saida = (Resolve-Path $Saida).Path

Passo "Limpando execucao anterior"
foreach ($p in 'repo', 'workspace', 'staging', 'destino', 'logs', 'estado', 'gatilhos') {
    $caminho = Join-Path $Raiz $p
    if (Test-Path $caminho) { Remove-Item $caminho -Recurse -Force }
}
Ok $Raiz

Passo "Criando o repositorio de teste"
$repo = Join-Path $Raiz 'repo'
New-Item -ItemType Directory -Path $repo -Force | Out-Null
Push-Location $repo
try {
    & git init -q -b HML .
    & git config user.name  'Fulano de Tal'
    & git config user.email 'fulano@teste.invalid'
    Set-Content -Path (Join-Path $repo 'README.txt') -Value 'projeto de teste do UnityLocalCI' -Encoding utf8
    & git add . | Out-Null
    & git commit -q -m 'primeiro commit do teste'
    $sha = (& git rev-parse --short HEAD)
    Ok "$repo  (branch HML, commit $sha)"
} finally { Pop-Location }

Passo "Criando as pastas"
foreach ($p in 'workspace', 'staging', 'destino', 'logs', 'estado', 'gatilhos') {
    New-Item -ItemType Directory -Path (Join-Path $Raiz $p) -Force | Out-Null
}
Ok "workspace, staging, destino, logs, estado, gatilhos"

Passo "Escrevendo a configuracao de teste"

# Intervalos curtos de proposito: esperar 60s de polling e 2min de debounce
# tornaria o teste manual insuportavel.
$config = [ordered]@{
    Scheduler = [ordered]@{
        MaxConcurrentBuilds     = 2
        MinFreeRamGb            = 1
        GlobalStatusFile        = Join-Path $Raiz 'destino\_STATUS-GERAL.txt'
        ResourceRecheckSeconds  = 5
        PendingCopyRetryMinutes = 1
    }
    State = [ordered]@{
        DatabasePath = Join-Path $Raiz 'estado\unitylocalci.db'
    }
    Defaults = [ordered]@{
        Watcher    = [ordered]@{ PollIntervalSeconds = 10; DebounceSeconds = 5; HookSignalPort = 8081 }
        Unity      = [ordered]@{ EditorVersion = '2022.3.62f3'; BuildTarget = 'WebGL'; TimeoutMinutes = 5 }
        Publishing = [ordered]@{ StagingFolder = Join-Path $Raiz 'staging' }
        Retention  = [ordered]@{ KeepLastBuilds = 5; MinFreeDiskGb = 1 }
    }
    Projects = @(
        [ordered]@{
            Name       = 'Teste'
            Enabled    = $true
            Repository = [ordered]@{
                Url               = $repo
                Branch            = 'HML'
                WorkspacePath     = Join-Path $Raiz 'workspace'
                PatCredentialName = $null   # repositorio local: nao precisa de PAT
            }
            Publishing        = [ordered]@{ ArtifactFolder = Join-Path $Raiz 'destino' }
            ManualTriggerFile = Join-Path $Raiz 'gatilhos\teste.txt'
        }
    )
    Notifications = [ordered]@{ TeamsWebhookCredentialName = $null }
}

$destinoConfig = Join-Path $Saida 'appsettings.local.json'
$config | ConvertTo-Json -Depth 8 | Set-Content -Path $destinoConfig -Encoding utf8
Ok $destinoConfig

Write-Host ""
Write-Host "Pronto." -ForegroundColor Green
Write-Host ""
Write-Host "Abra a janela com:"
Write-Host "    dotnet run --project src\UnityLocalCI.Worker" -ForegroundColor Yellow
Write-Host ""
Write-Host "O que dá para testar:"
Write-Host "  1. A build dispara sozinha em ~15s e FALHA. Isso e o esperado:"
Write-Host "     a pasta nao e um projeto Unity. Veja o erro na aba Builds."
Write-Host "  2. Botao 'Construir agora' na aba Projetos."
Write-Host "  3. Gatilho por arquivo:"
Write-Host "        echo x > $Raiz\gatilhos\teste.txt"
Write-Host "  4. Sinal em loopback:"
Write-Host "        curl --data Teste http://127.0.0.1:8081/"
Write-Host "  5. Commit novo dispara build:"
Write-Host "        cd $repo; echo mudanca >> README.txt; git commit -aqm 'mudanca'"
Write-Host "  6. Os arquivos que o time le:"
Write-Host "        explorer $Raiz\destino"
Write-Host ""
Write-Host "Para voltar ao normal, apague:"
Write-Host "    $destinoConfig"
