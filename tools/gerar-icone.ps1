<#
    Gera o icone do UnityLocalCI.

    Roda uma vez so; o .ico resultante e versionado. Esta aqui para o desenho
    poder ser refeito sem depender de ninguem ter um editor de imagem.

    O formato ICO aceita PNG embutido a partir do Vista, entao cada tamanho e
    um PNG completo — bem mais simples que montar bitmaps DIB na mao.
#>
param(
    [string]$Destino = (Join-Path $PSScriptRoot '..\src\UnityLocalCI.Worker\unitylocalci.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$tamanhos = 16, 20, 24, 32, 48, 64, 128, 256

$fundo   = [System.Drawing.Color]::FromArgb(0x0E, 0x63, 0x9C)  # azul do tema
$marca   = [System.Drawing.Color]::FromArgb(0xFF, 0xFF, 0xFF)
$destaque = [System.Drawing.Color]::FromArgb(0x89, 0xD1, 0x85) # verde de sucesso

function Desenhar([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Quadrado arredondado de fundo. O raio acompanha o tamanho para o icone
    # nao virar um circulo em 16px nem um quadrado seco em 256px.
    $raio = [Math]::Max(2, [int]($s * 0.22))
    $r = New-Object System.Drawing.Rectangle 0, 0, ($s - 1), ($s - 1)
    $caminho = New-Object System.Drawing.Drawing2D.GraphicsPath
    $caminho.AddArc($r.X, $r.Y, $raio, $raio, 180, 90)
    $caminho.AddArc(($r.Right - $raio), $r.Y, $raio, $raio, 270, 90)
    $caminho.AddArc(($r.Right - $raio), ($r.Bottom - $raio), $raio, $raio, 0, 90)
    $caminho.AddArc($r.X, ($r.Bottom - $raio), $raio, $raio, 90, 90)
    $caminho.CloseFigure()

    $pincel = New-Object System.Drawing.SolidBrush $fundo
    $g.FillPath($pincel, $caminho)

    # Seta para cima: build que sobe, publicacao. Formas cheias e grossas,
    # porque em 16px qualquer detalhe fino vira borrao.
    $cx = $s / 2.0
    $larguraSeta = $s * 0.46
    $pontos = @(
        (New-Object System.Drawing.PointF $cx, ($s * 0.20)),
        (New-Object System.Drawing.PointF ($cx + $larguraSeta / 2), ($s * 0.50)),
        (New-Object System.Drawing.PointF ($cx + $larguraSeta * 0.18), ($s * 0.50)),
        (New-Object System.Drawing.PointF ($cx + $larguraSeta * 0.18), ($s * 0.72)),
        (New-Object System.Drawing.PointF ($cx - $larguraSeta * 0.18), ($s * 0.72)),
        (New-Object System.Drawing.PointF ($cx - $larguraSeta * 0.18), ($s * 0.50)),
        (New-Object System.Drawing.PointF ($cx - $larguraSeta / 2), ($s * 0.50))
    )
    $pincelMarca = New-Object System.Drawing.SolidBrush $marca
    $g.FillPolygon($pincelMarca, [System.Drawing.PointF[]]$pontos)

    # Base verde: o "publicado". Some nos tamanhos pequenos de proposito.
    if ($s -ge 32) {
        $pincelBase = New-Object System.Drawing.SolidBrush $destaque
        $g.FillRectangle($pincelBase, ($cx - $larguraSeta / 2), ($s * 0.78), $larguraSeta, ($s * 0.08))
        $pincelBase.Dispose()
    }

    $pincel.Dispose(); $pincelMarca.Dispose(); $caminho.Dispose(); $g.Dispose()
    return $bmp
}

# --- monta o arquivo ICO -----------------------------------------------------

<#
    Ate 48px as entradas sao DIB de 32 bits, e nao PNG.

    PNG embutido e valido desde o Vista e a shell do Windows o entende, mas o
    GDI+ do System.Drawing nao decodifica essas entradas: um Icon montado a
    partir delas falha ao desenhar. Como e exatamente o System.Drawing que
    carrega o icone da bandeja, os tamanhos pequenos precisam ser DIB. Em 256px
    o PNG vale a pena pelo tamanho do arquivo, e nada o desenha pelo GDI+.
#>
function ParaDib([System.Drawing.Bitmap]$bmp) {
    $lado = $bmp.Width
    $fluxo = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $fluxo

    # BITMAPINFOHEADER. A altura e o dobro porque o formato espera a mascara
    # AND logo depois da imagem, ainda que ela va vazia com alfa de 32 bits.
    $w.Write([UInt32]40)
    $w.Write([Int32]$lado)
    $w.Write([Int32]($lado * 2))
    $w.Write([UInt16]1)
    $w.Write([UInt16]32)
    $w.Write([UInt32]0)                      # BI_RGB
    $w.Write([UInt32]($lado * $lado * 4))
    $w.Write([Int32]0); $w.Write([Int32]0)
    $w.Write([UInt32]0); $w.Write([UInt32]0)

    # Pixels BGRA, de baixo para cima. Lidos de uma vez por LockBits: o laco
    # pixel a pixel em PowerShell alem de lento produzia dados truncados.
    $area = New-Object System.Drawing.Rectangle 0, 0, $lado, $lado
    $trava = $bmp.LockBits($area, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                           [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $passo = $trava.Stride
        $linha = New-Object Byte[] $passo

        for ($y = $lado - 1; $y -ge 0; $y--) {
            $origem = [IntPtr]::Add($trava.Scan0, $y * $passo)
            [System.Runtime.InteropServices.Marshal]::Copy($origem, $linha, 0, $passo)
            $w.Write($linha, 0, ($lado * 4))
        }
    } finally {
        $bmp.UnlockBits($trava)
    }

    # Mascara AND zerada: a transparencia vem do canal alfa. Cada linha e
    # alinhada em 4 bytes.
    $bytesPorLinha = [int]([Math]::Floor(($lado + 31) / 32) * 4)
    $vazia = New-Object Byte[] $bytesPorLinha
    for ($y = 0; $y -lt $lado; $y++) { $w.Write($vazia, 0, $bytesPorLinha) }

    $w.Flush()
    $dados = $fluxo.ToArray()
    $w.Dispose(); $fluxo.Dispose()
    # Virgula a frente: sem ela o PowerShell desenrola o array no retorno e o
    # chamador recebe bytes soltos em vez de um Byte[].
    return ,$dados
}

$pngs = @()
foreach ($s in $tamanhos) {
    $bmp = Desenhar $s

    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += ,@($s, $ms.ToArray())
        $ms.Dispose()
    } else {
        $pngs += ,@($s, (ParaDib $bmp))
    }

    $bmp.Dispose()
}

$saida = New-Object System.IO.MemoryStream
$escritor = New-Object System.IO.BinaryWriter $saida

# ICONDIR
$escritor.Write([UInt16]0)                 # reservado
$escritor.Write([UInt16]1)                 # tipo: icone
$escritor.Write([UInt16]$pngs.Count)

# ICONDIRENTRY: 6 bytes de cabecalho + 16 por entrada
$deslocamento = 6 + (16 * $pngs.Count)
foreach ($item in $pngs) {
    $lado = $item[0]; $dados = $item[1]
    # 256 e gravado como 0 no formato
    $escritor.Write([Byte]($(if ($lado -ge 256) { 0 } else { $lado })))
    $escritor.Write([Byte]($(if ($lado -ge 256) { 0 } else { $lado })))
    $escritor.Write([Byte]0)               # cores na paleta
    $escritor.Write([Byte]0)               # reservado
    $escritor.Write([UInt16]1)             # planos
    $escritor.Write([UInt16]32)            # bits por pixel
    $escritor.Write([UInt32]$dados.Length)
    $escritor.Write([UInt32]$deslocamento)
    $deslocamento += $dados.Length
}

foreach ($item in $pngs) { $escritor.Write($item[1]) }

$escritor.Flush()
[System.IO.File]::WriteAllBytes($Destino, $saida.ToArray())
$escritor.Dispose(); $saida.Dispose()

$tamanho = (Get-Item $Destino).Length
Write-Host "Icone gravado em $Destino ($tamanho bytes, $($pngs.Count) tamanhos)"
