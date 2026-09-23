<#
    Instala o UnityLocalCI nesta maquina.

    Nao pede administrador: instala em %LOCALAPPDATA%, cria os atalhos do menu
    Iniciar e da area de trabalho, e liga o inicio automatico com o Windows.

    Dois modos:

      instalar.ps1
          usa a pasta 'publicado' ao lado, gerada por tools\publicar.ps1

      instalar.ps1 -DeUrl https://...UnityLocalCI.zip
          baixa o pacote e instala. Para uma release privada do GitHub, passe
          tambem -Token com um PAT que tenha escopo de leitura do repositorio.

    Para desinstalar:  instalar.ps1 -Desinstalar
#>
param(
    [string]$Origem,
    [string]$DeUrl,
    [string]$Token,
    [string]$Destino = (Join-Path $env:LOCALAPPDATA 'UnityLocalCI'),
    [switch]$SemAtalhos,
    [switch]$SemInicioAutomatico,
    [switch]$Desinstalar
)

$ErrorActionPreference = 'Stop'

$NomeExe   = 'UnityLocalCI.exe'
$ChaveRun  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$ValorRun  = 'UnityLocalCI'

function Passo($t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }
function Ok($t)    { Write-Host "   [ok] $t" }
function Aviso($t) { Write-Host "   [!]  $t" -ForegroundColor Yellow }
function Erro($t)  { Write-Host "   [ERRO] $t" -ForegroundColor Red; exit 1 }

function CaminhoAtalho($pasta) { Join-Path $pasta 'UnityLocalCI.lnk' }
$menuIniciar = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$areaTrabalho = [Environment]::GetFolderPath('Desktop')

# ------------------------------------------------------------- desinstalacao

if ($Desinstalar) {
    Passo "Desinstalando"

    Get-Process -Name 'UnityLocalCI' -ErrorAction SilentlyContinue | ForEach-Object {
        $_.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 800
        if (-not $_.HasExited) { $_ | Stop-Process -Force }
        Ok "processo encerrado"
    }

    Remove-ItemProperty -Path $ChaveRun -Name $ValorRun -ErrorAction SilentlyContinue
    Ok "inicio automatico removido"

    foreach ($p in $menuIniciar, $areaTrabalho) {
        $lnk = CaminhoAtalho $p
        if (Test-Path $lnk) { Remove-Item $lnk -Force; Ok "atalho removido de $p" }
    }

    if (Test-Path $Destino) {
        Remove-Item $Destino -Recurse -Force
        Ok "arquivos removidos de $Destino"
    }

    Write-Host ""
    Write-Host "Desinstalado." -ForegroundColor Green
    Write-Host "O estado, os logs e os artefatos em C:\ci (ou onde estiverem configurados) NAO foram apagados."
    exit 0
}

# ------------------------------------------------------------------- origem

Passo "Obtendo os arquivos"

$temporario = $null

if ($DeUrl) {
    $temporario = Join-Path ([IO.Path]::GetTempPath()) ("unitylocalci-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporario -Force | Out-Null
    $zip = Join-Path $temporario 'UnityLocalCI.zip'

    $cabecalhos = @{}
    if ($Token) {
        # Release privada: a API do GitHub aceita o PAT e devolve o binario.
        $cabecalhos['Authorization'] = "Bearer $Token"
        $cabecalhos['Accept'] = 'application/octet-stream'
    }

    Write-Host "   baixando de $DeUrl"
    try {
        # TLS 1.2 explicito: o Windows PowerShell 5.1 ainda negocia TLS 1.0 por
        # padrao em algumas maquinas, e o GitHub recusa.
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $DeUrl -OutFile $zip -Headers $cabecalhos -UseBasicParsing
    } catch {
        Erro "download falhou: $($_.Exception.Message)"
    }

    Expand-Archive -Path $zip -DestinationPath $temporario -Force
    Remove-Item $zip -Force
    $Origem = $temporario
    Ok "baixado e extraido"
}

if (-not $Origem) { $Origem = Join-Path $PSScriptRoot '..\publicado' }
if (-not (Test-Path $Origem)) {
    Erro "pacote nao encontrado em $Origem. Gere com tools\publicar.ps1, ou use -DeUrl."
}
$Origem = (Resolve-Path $Origem).Path

if (-not (Test-Path (Join-Path $Origem $NomeExe))) {
    Erro "$NomeExe nao encontrado em $Origem."
}
Ok $Origem

# ------------------------------------------------------------------ copiar

Passo "Instalando em $Destino"

Get-Process -Name 'UnityLocalCI' -ErrorAction SilentlyContinue | ForEach-Object {
    Aviso "ha uma instancia rodando; encerrando para substituir os arquivos"

    # O Unity roda como filho do servico, num job object: encerrar o servico
    # mata a build junto. Ela volta como Interrompida e e reenfileirada no
    # proximo start, mas o Library fica pela metade — e uma build cancelada no
    # meio da importacao envenena o cache das proximas.
    if (Get-Process -Name 'Unity' -ErrorAction SilentlyContinue) {
        Aviso "ha Unity aberto nesta maquina; se for uma build do CI, ela sera interrompida"
    }

    $_.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 1200
    if (-not $_.HasExited) { $_ | Stop-Process -Force }
}

# A configuracao do usuario nao pode ser sobrescrita por uma atualizacao.
$configExistente = Join-Path $Destino 'appsettings.json'
$preservada = $null
if (Test-Path $configExistente) {
    $preservada = Get-Content $configExistente -Raw
    Aviso "appsettings.json existente sera preservado"
}

New-Item -ItemType Directory -Path $Destino -Force | Out-Null
Copy-Item (Join-Path $Origem '*') $Destino -Recurse -Force -Exclude 'projetos'

# A pasta com um arquivo por projeto e do usuario, nunca do pacote: uma
# atualizacao que a sobrescrevesse apagaria a configuracao dos projetos.
$projetosNoPacote = Join-Path $Origem 'projetos'
$projetosInstalados = Join-Path $Destino 'projetos'
if ((Test-Path $projetosNoPacote) -and -not (Test-Path $projetosInstalados)) {
    Copy-Item $projetosNoPacote $Destino -Recurse -Force
}
if (Test-Path $projetosInstalados) {
    $quantos = @(Get-ChildItem $projetosInstalados -Filter *.json -ErrorAction SilentlyContinue).Count
    Aviso "$quantos projeto(s) preservados em projetos\"
}

if ($preservada) { Set-Content -Path $configExistente -Value $preservada -Encoding utf8 -NoNewline }

if ($temporario -and (Test-Path $temporario)) { Remove-Item $temporario -Recurse -Force }

$exe = Join-Path $Destino $NomeExe
Ok $exe

# ----------------------------------------------------------------- atalhos

if (-not $SemAtalhos) {
    Passo "Atalhos"

    $shell = New-Object -ComObject WScript.Shell
    foreach ($pasta in $menuIniciar, $areaTrabalho) {
        $caminho = CaminhoAtalho $pasta
        $lnk = $shell.CreateShortcut($caminho)
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = $Destino
        $lnk.IconLocation = $exe
        $lnk.Description = 'CI local para projetos Unity'
        $lnk.Save()

        # O atalho e relido depois de gravado.
        #
        # Rodar o instalador de dentro de um aplicativo empacotado (MSIX, ou um
        # terminal dentro de um) faz o Windows redirecionar escritas em
        # %LOCALAPPDATA% para dentro do pacote, e o atalho sai apontando para
        # uma copia em ...\AppData\Local\Packages\<pacote>\LocalCache\.
        # A copia funciona, mas e invisivel para o resto da maquina — e o
        # antivirus corporativo a trata como programa desconhecido, porque ela
        # nao esta onde um programa instalado deveria estar.
        $gravado = $shell.CreateShortcut($caminho).TargetPath
        if ($gravado -ne $exe) {
            Aviso "o atalho em $pasta ficou apontando para:"
            Aviso "   $gravado"
            Aviso "Rode este instalador num PowerShell comum, fora de terminal embutido em aplicativo."
        } else {
            Ok $pasta
        }
    }
    [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
}

# -------------------------------------------------------- inicio automatico

if (-not $SemInicioAutomatico) {
    Passo "Inicio automatico"

    # Aspas no caminho: %LOCALAPPDATA% costuma ter espaco no nome do usuario, e
    # sem elas a shell corta no primeiro espaco.
    New-Item -Path $ChaveRun -Force | Out-Null
    Set-ItemProperty -Path $ChaveRun -Name $ValorRun -Value "`"$exe`""
    Ok "o app abrira junto com o Windows"
    Write-Host "      (desligue pelo menu do icone na bandeja, ou rode com -SemInicioAutomatico)"
}

# ------------------------------------------------------------------- final

Passo "Verificando"

$naoPreenchidos = 0
$config = Join-Path $Destino 'appsettings.json'
if (Test-Path $config) {
    $naoPreenchidos = ([regex]::Matches((Get-Content $config -Raw), 'PREENCHER')).Count
}

if ($naoPreenchidos -gt 0) {
    Aviso "$naoPreenchidos campo(s) ainda com PREENCHER em appsettings.json"
    Aviso "O servico nao vai construir nada ate eles serem ajustados."
    Aviso "Ajuste pela aba Configuracao do proprio app."
} else {
    Ok "configuracao sem placeholders"
}

Write-Host ""
Write-Host "Instalado." -ForegroundColor Green
Write-Host ""
Write-Host "  Executavel .....: $exe"
Write-Host "  Atalhos ........: menu Iniciar e area de trabalho"
Write-Host "  Inicio automatico: $(if ($SemInicioAutomatico) { 'desligado' } else { 'ligado' })"
Write-Host ""
Write-Host "Fechar a janela no X esconde o app na bandeja e as builds continuam."
Write-Host "Para sair de verdade, botao direito no icone da bandeja e Sair."
Write-Host ""
Write-Host "Para desinstalar:  instalar.ps1 -Desinstalar"

# O instalador nao abre o programa.
#
# Ele abria, com Start-Process, e o antivirus corporativo passou a marcar a
# relacao: um executavel sem assinatura de fornecedor iniciado pelo
# powershell.exe e um padrao que o Behavior Monitoring do Apex One registra e
# bloqueia ("Detectado programa recem-encontrado"). Aberto pelo atalho, quem
# inicia e o Explorer, que e como um programa de area de trabalho comeca.
#
# Nada aqui engana o antivirus: o executavel e o mesmo, no mesmo lugar. O que
# muda e o instalador parar de lancar o programa de um jeito que nenhum
# instalador de verdade usa.
Write-Host ""
Write-Host "Abra o BuildMaker pelo atalho da area de trabalho ou do menu Iniciar."
