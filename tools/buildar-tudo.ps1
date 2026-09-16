<#
    Toca o ManualTriggerFile de cada projeto habilitado.

    O scheduler distribui conforme as vagas: tocar N arquivos nao dispara N
    builds simultaneas, dispara N jobs que respeitam o teto global.
#>
param(
    [string]$Config = (Join-Path $PSScriptRoot '..\src\UnityLocalCI.Worker\appsettings.json'),
    [string[]]$Projeto
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Config)) {
    Write-Host "ERRO: configuracao nao encontrada em $Config"
    Write-Host "Passe o caminho: buildar-tudo.bat -Config C:\caminho\appsettings.json"
    exit 1
}

$json = Get-Content $Config -Raw | ConvertFrom-Json
$projetos = @($json.Projects | Where-Object { $_.Enabled -ne $false })

if ($Projeto) {
    $projetos = @($projetos | Where-Object { $Projeto -contains $_.Name })
}

if ($projetos.Count -eq 0) {
    Write-Host "Nenhum projeto habilitado encontrado."
    exit 1
}

$tocados = 0
foreach ($p in $projetos) {
    $arquivo = $p.ManualTriggerFile

    if ([string]::IsNullOrWhiteSpace($arquivo)) {
        Write-Host "  $($p.Name): sem ManualTriggerFile configurado, ignorado."
        continue
    }

    $pasta = Split-Path $arquivo -Parent
    if ($pasta -and -not (Test-Path $pasta)) { New-Item -ItemType Directory -Path $pasta -Force | Out-Null }

    # Set-Content e nao New-Item -Force: o servico apaga o arquivo ao consumir,
    # entao ele normalmente nao existe, e recriar e o caso comum.
    Set-Content -Path $arquivo -Value (Get-Date -Format o) -Encoding utf8
    Write-Host "  $($p.Name): gatilho acionado."
    $tocados++
}

Write-Host ""
Write-Host "$tocados projeto(s) na fila. Acompanhe pelo _STATUS-GERAL.txt."
