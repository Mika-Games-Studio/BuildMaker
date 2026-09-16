<#
    Grava os segredos do UnityLocalCI no Windows Credential Manager.

    O servico apenas le. A configuracao guarda so o NOME da credencial, nunca o
    valor, entao nada de segredo entra em appsettings.json, log ou historico do
    Git.

    IMPORTANTE: o Credential Manager e por usuario, nao por maquina. Rode este
    script logado como a conta que vai executar o servico.
#>
param(
    [string]$Config = (Join-Path $PSScriptRoot '..\src\UnityLocalCI.Worker\appsettings.json')
)

$ErrorActionPreference = 'Stop'

function Gravar([string]$nome, [string]$descricao) {
    if ([string]::IsNullOrWhiteSpace($nome)) { return }

    Write-Host ""
    Write-Host "  $nome"
    Write-Host "  $descricao"

    $existente = & cmdkey /list:$nome 2>$null | Select-String -SimpleMatch $nome
    if ($existente) {
        $resposta = Read-Host "  Ja existe. Substituir? (s/N)"
        if ($resposta -notmatch '^[sS]') { Write-Host "  Mantida."; return }
    }

    # Read-Host -AsSecureString para o valor nao aparecer na tela nem no
    # historico do console.
    $seguro = Read-Host "  Valor" -AsSecureString
    if ($seguro.Length -eq 0) { Write-Host "  Vazio, pulado."; return }

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($seguro)
    try {
        $valor = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        & cmdkey /generic:$nome /user:unitylocalci /pass:$valor | Out-Null
        Write-Host "  Gravada."
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

if (-not (Test-Path $Config)) {
    Write-Host "ERRO: configuracao nao encontrada em $Config"
    exit 1
}

$json = Get-Content $Config -Raw | ConvertFrom-Json

Write-Host "UnityLocalCI - segredos"
Write-Host "Usuario atual: $env:USERDOMAIN\$env:USERNAME"
Write-Host "O servico precisa rodar sob esta mesma conta para enxergar o que for gravado aqui."

$nomes = [System.Collections.Generic.HashSet[string]]::new()
foreach ($p in $json.Projects) {
    if ($p.Repository.PatCredentialName) { [void]$nomes.Add($p.Repository.PatCredentialName) }
}

foreach ($nome in $nomes) {
    Gravar $nome "PAT do Azure DevOps, escopo Code: Read."
}

if ($json.Notifications.TeamsWebhookCredentialName) {
    Gravar $json.Notifications.TeamsWebhookCredentialName "URL do webhook do Teams."
}

Write-Host ""
Write-Host "Pronto. Confira com: cmdkey /list"
