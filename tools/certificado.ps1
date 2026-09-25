<#
    Cria o certificado que assina o BuildMaker e faz esta maquina confiar nele.

    Roda uma vez por maquina. Depois disso, tools\publicar.ps1 assina o
    executavel sozinho a cada publicacao.

    POR QUE ASSINAR

    Um executavel sem assinatura nao tem quem responda por ele. O Windows o
    trata como programa desconhecido, o SmartScreen avisa, e o antivirus
    corporativo — aqui, o Behavior Monitoring do Apex One — bloqueia a execucao
    com um dialogo. E cada build novo tem hash novo, entao o bloqueio volta a
    cada publicacao. Assinatura resolve isso pela raiz: e alguem dizendo "este
    programa e meu, e eu respondo por ele".

    O QUE ESTE SCRIPT FAZ, E O QUE ISSO CUSTA

    O certificado e auto-assinado: quem responde pelo programa e voce, nesta
    maquina, e nao uma autoridade certificadora. Para o Windows aceitar isso, o
    certificado precisa entrar em dois lugares do SEU perfil de usuario:

      Raiz Confiavel      faz a cadeia da assinatura ser valida
      Editores Confiaveis marca o publicador como conhecido

    Isso e uma decisao de confianca de verdade, e vale entender o alcance:
    dali em diante, o seu usuario confia em QUALQUER programa assinado com esta
    chave. A chave vive no seu perfil, protegida pelo Windows, e nunca sai
    daqui — mas quem a obtiver pode assinar algo que a sua maquina aceitara sem
    perguntar. E por isso que este passo esta num script que voce roda, e nao
    escondido dentro do publicar.

    NAO E O MESMO QUE UM CERTIFICADO DE VERDADE

    Um certificado auto-assinado vale nesta maquina, para o seu usuario. Ele
    nao vale em nenhuma outra, e uma politica corporativa pode exigir uma
    autoridade certificadora reconhecida e recusa-lo assim mesmo. Para o
    programa ser confiavel em qualquer lugar — e para o SmartScreen parar de
    avisar — o caminho e um certificado OV ou EV comprado de uma CA, que e o
    que Discord e VS Code usam.

    Para remover:  certificado.ps1 -Remover
#>
param(
    [switch]$Remover
)

$ErrorActionPreference = 'Stop'

$Assunto = 'CN=Mika Games Studio, O=Mika Games Studio, C=BR'
$Lojas   = 'Root', 'TrustedPublisher'

function Passo($t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }
function Ok($t)    { Write-Host "   [ok] $t" }
function Aviso($t) { Write-Host "   [!]  $t" -ForegroundColor Yellow }

function Meu {
    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Assunto -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

# ----------------------------------------------------------------- remocao

if ($Remover) {
    Passo "Removendo o certificado"

    foreach ($nome in $Lojas) {
        $loja = New-Object Security.Cryptography.X509Certificates.X509Store($nome, 'CurrentUser')
        $loja.Open('ReadWrite')
        $achados = @($loja.Certificates | Where-Object Subject -eq $Assunto)
        foreach ($c in $achados) { $loja.Remove($c) }
        $loja.Close()
        Ok "${nome}: $($achados.Count) removido(s)"
    }

    Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq $Assunto | ForEach-Object {
        Remove-Item $_.PSPath -Force
        Ok "chave privada removida ($($_.Thumbprint))"
    }

    Write-Host ""
    Write-Host "Removido. O que ja foi assinado continua assinado, mas a assinatura" -ForegroundColor Green
    Write-Host "deixa de ser reconhecida por esta maquina."
    exit 0
}

# ------------------------------------------------------------------ criacao

Passo "Certificado"

$cert = Meu
if ($cert) {
    Ok "ja existe: $($cert.Thumbprint)"
    Ok "valido ate $($cert.NotAfter.ToString('dd/MM/yyyy'))"
} else {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Assunto `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature -KeyLength 3072 `
        -KeyAlgorithm RSA -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(5) `
        -FriendlyName 'BuildMaker - assinatura de codigo'

    Ok "criado: $($cert.Thumbprint)"
    Ok "valido ate $($cert.NotAfter.ToString('dd/MM/yyyy'))"
}

# ------------------------------------------------------------------ confianca

Passo "Confianca desta maquina"

Write-Host "   Os proximos passos fazem o SEU usuario confiar em tudo que for assinado"
Write-Host "   com esta chave. O Windows pode pedir confirmacao numa caixa de dialogo."
Write-Host ""

# So a parte publica: a chave privada nunca sai do armazenamento do usuario.
$publico = Join-Path ([IO.Path]::GetTempPath()) 'mika-games-studio.cer'
[IO.File]::WriteAllBytes($publico, $cert.Export('Cert'))

try {
    foreach ($nome in $Lojas) {
        $loja = New-Object Security.Cryptography.X509Certificates.X509Store($nome, 'CurrentUser')
        $loja.Open('ReadWrite')

        if ($loja.Certificates | Where-Object Thumbprint -eq $cert.Thumbprint) {
            Ok "${nome}: ja estava la"
        } else {
            $loja.Add([Security.Cryptography.X509Certificates.X509Certificate2]::new($publico))
            Ok "${nome}: adicionado"
        }

        $loja.Close()
    }
} finally {
    Remove-Item $publico -Force -ErrorAction SilentlyContinue
}

# ------------------------------------------------------------------ conferindo

Passo "Conferindo"

$exe = Join-Path $PSScriptRoot '..\publicado\UnityLocalCI.exe'
if (Test-Path $exe) {
    $estado = (Get-AuthenticodeSignature $exe).Status
    if ($estado -eq 'Valid') {
        Ok "o executavel publicado esta assinado e a assinatura e reconhecida"
    } elseif ($estado -eq 'NotSigned') {
        Aviso "o executavel publicado ainda nao esta assinado; rode tools\publicar.ps1"
    } else {
        Aviso "assinatura presente, mas o Windows diz: $estado"
        Aviso "se o dialogo de confianca foi recusado, rode este script de novo"
    }
} else {
    Aviso "nao ha pacote em publicado\ para conferir"
}

Write-Host ""
Write-Host "Pronto." -ForegroundColor Green
Write-Host ""
Write-Host "Daqui em diante, tools\publicar.ps1 assina o executavel a cada publicacao."
Write-Host "Para desfazer:  certificado.ps1 -Remover"
