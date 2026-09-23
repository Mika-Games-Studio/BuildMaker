<#
    Instala o BuildMaker nesta maquina.

    Nao pede administrador: instala em %LOCALAPPDATA%, cria os atalhos do menu
    Iniciar e da area de trabalho, registra o programa em Aplicativos Instalados
    (para aparecer na busca do Windows e poder ser desinstalado por la) e liga o
    inicio automatico pela chave Run do usuario.

    A chave Run, e nao uma tarefa agendada, porque e ela que aparece na aba
    Inicializar do Gerenciador de Tarefas — onde qualquer um desliga o inicio
    automatico com um clique, sem precisar saber que existe um Agendador de
    Tarefas.

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

# O arquivo continua UnityLocalCI.exe: o nome do executavel e identidade, e
# renomea-lo quebraria o servico e a credencial ja gravados. O que o Windows
# mostra vem da informacao de versao do proprio binario, que diz BuildMaker.
$NomeExe    = 'UnityLocalCI.exe'

$ChaveRun      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$ValorRun      = 'BuildMaker'
$ValorRunAntigo = 'UnityLocalCI'
$NomeTarefa    = 'BuildMaker'

# A chave que alimenta Configuracoes > Aplicativos > Aplicativos instalados. Em
# HKCU porque a instalacao e por usuario, em %LOCALAPPDATA%: registrar em HKLM
# anunciaria para todos os usuarios da maquina um programa que so existe para um.
$ChaveDesinstalar = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BuildMaker'

function Passo($t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }
function Ok($t)    { Write-Host "   [ok] $t" }
function Aviso($t) { Write-Host "   [!]  $t" -ForegroundColor Yellow }
function Erro($t)  { Write-Host "   [ERRO] $t" -ForegroundColor Red; exit 1 }

function EhAdministrador {
    $identidade = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $identidade).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# O nome do atalho e o que aparece na area de trabalho e na busca do Windows.
function CaminhoAtalho($pasta) { Join-Path $pasta 'BuildMaker.lnk' }

# O nome anterior, para a instalacao nova nao deixar dois atalhos para o mesmo
# programa na area de trabalho.
function CaminhoAtalhoAntigo($pasta) { Join-Path $pasta 'UnityLocalCI.lnk' }
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
    if (Get-ScheduledTask -TaskName $NomeTarefa -ErrorAction SilentlyContinue) {
        if (EhAdministrador) {
            Unregister-ScheduledTask -TaskName $NomeTarefa -Confirm:$false
            Ok "tarefa de inicio automatico removida"
        } else {
            Aviso "a tarefa '$NomeTarefa' ficou para tras: remove-la exige administrador"
            Aviso "rode de novo num PowerShell como administrador, ou apague pelo Agendador de Tarefas"
        }
    } else {
        Ok "inicio automatico removido"
    }

    Remove-Item $ChaveDesinstalar -Recurse -Force -ErrorAction SilentlyContinue
    Ok "registro em Aplicativos Instalados removido"

    foreach ($p in $menuIniciar, $areaTrabalho) {
        foreach ($lnk in (CaminhoAtalho $p), (CaminhoAtalhoAntigo $p)) {
            if (Test-Path $lnk) { Remove-Item $lnk -Force; Ok "atalho removido de $p" }
        }
    }

    if (Test-Path $Destino) {
        Remove-Item $Destino -Recurse -Force
        Ok "arquivos removidos de $Destino"
    }

    Write-Host ""
    Write-Host "Desinstalado." -ForegroundColor Green
    Write-Host "O historico em %LOCALAPPDATA%\BuildMaker e os artefatos das builds NAO foram apagados."
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
        # O atalho com o nome antigo sai: senao ficariam dois, apontando para o
        # mesmo programa, e a busca do Windows mostraria os dois.
        $antigo = CaminhoAtalhoAntigo $pasta
        if (Test-Path $antigo) { Remove-Item $antigo -Force }

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

Passo "Aplicativos Instalados"

# Sem esta chave o programa nao aparece em Configuracoes > Aplicativos, nao e
# encontrado pela busca do Windows e nao tem como ser desinstalado a nao ser
# rodando este script na mao — que e como ele estava ate agora.
$tamanhoKb = [int]((Get-ChildItem $Destino -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
$versao = (Get-Item $exe).VersionInfo.FileVersion
if (-not $versao) { $versao = '1.0.0' }

$desinstalador = "powershell.exe -ExecutionPolicy Bypass -File `"$(Join-Path $Destino 'instalar.ps1')`" -Desinstalar"

New-Item -Path $ChaveDesinstalar -Force | Out-Null
$entradas = @{
    DisplayName     = 'BuildMaker'
    DisplayVersion  = $versao
    Publisher       = 'BSA Tech'
    DisplayIcon     = $exe
    InstallLocation = $Destino
    UninstallString = $desinstalador
    EstimatedSize   = $tamanhoKb
    NoModify        = 1
    NoRepair        = 1
}
foreach ($nome in $entradas.Keys) {
    $tipo = if ($entradas[$nome] -is [int]) { 'DWord' } else { 'String' }
    New-ItemProperty -Path $ChaveDesinstalar -Name $nome -Value $entradas[$nome] -PropertyType $tipo -Force | Out-Null
}

# O desinstalador precisa existir depois que a pasta de origem sumir.
Copy-Item $PSCommandPath (Join-Path $Destino 'instalar.ps1') -Force
Ok "registrado como 'BuildMaker' em Aplicativos Instalados"

# -------------------------------------------------------- inicio automatico

if (-not $SemInicioAutomatico) {
    Passo "Inicio automatico"

    # Uma versao intermediaria usou tarefa agendada, porque o programa pedia
    # elevacao e o Windows nao inicia programa elevado pela chave Run. Foi
    # revertido: tarefa agendada nao aparece na aba Inicializar do Gerenciador de
    # Tarefas, e o inicio automatico virava algo que so se desliga sabendo que
    # existe um Agendador de Tarefas e onde ele fica.
    if (Get-ScheduledTask -TaskName $NomeTarefa -ErrorAction SilentlyContinue) {
        if (EhAdministrador) {
            Unregister-ScheduledTask -TaskName $NomeTarefa -Confirm:$false
            Ok "tarefa agendada de uma versao anterior removida"
        } else {
            Aviso "ha uma tarefa agendada '$NomeTarefa' de uma versao anterior desta ferramenta"
            Aviso "com ela e a chave Run, o programa abriria duas vezes no logon"
            Aviso "rode este instalador uma vez como administrador, ou apague a tarefa"
        }
    }

    # O nome antigo do valor sai junto: com os dois, a aba Inicializar mostraria
    # duas entradas para o mesmo programa.
    Remove-ItemProperty -Path $ChaveRun -Name $ValorRunAntigo -ErrorAction SilentlyContinue

    # Aspas no caminho: %LOCALAPPDATA% costuma ter espaco no nome do usuario, e
    # sem elas a shell corta no primeiro espaco.
    New-Item -Path $ChaveRun -Force | Out-Null
    Set-ItemProperty -Path $ChaveRun -Name $ValorRun -Value "`"$exe`""
    Ok "o app abrira junto com o Windows"
    Write-Host "      (desligue pela aba Inicializar do Gerenciador de Tarefas, pelo menu"
    Write-Host "       do icone na bandeja, ou instale com -SemInicioAutomatico)"
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
Write-Host "  Atalhos ........: menu Iniciar e area de trabalho, como 'BuildMaker'"
Write-Host "  Aplicativos ....: aparece como 'BuildMaker', com desinstalar"
Write-Host "  Inicio automatico: $(if ($SemInicioAutomatico) { 'desligado' } else { 'ligado' })"
Write-Host ""
Write-Host "Fechar a janela no X esconde o app na bandeja e as builds continuam."
Write-Host "Para sair de verdade, botao direito no icone da bandeja e Sair."
Write-Host ""
Write-Host "Para desinstalar: por Configuracoes > Aplicativos, ou instalar.ps1 -Desinstalar"

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
