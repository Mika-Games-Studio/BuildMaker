<#
    Gera o icone do BuildMaker.

    Roda uma vez so; o .ico resultante e versionado. Esta aqui para o desenho
    poder ser refeito sem depender de ninguem ter um editor de imagem.

    A geometria da marca esta duplicada em src\UnityLocalCI.Worker\BrandMark.cs,
    que desenha a mesma coisa dentro do aplicativo. Nao da para reaproveitar:
    este script roda antes de o projeto compilar. Quem mexer numa tabela precisa
    mexer na outra.

    O formato ICO aceita PNG embutido a partir do Vista, entao cada tamanho e
    um PNG completo — bem mais simples que montar bitmaps DIB na mao.
#>
param(
    [string]$Destino = (Join-Path $PSScriptRoot '..\src\UnityLocalCI.Worker\unitylocalci.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$tamanhos = 16, 20, 24, 32, 48, 64, 128, 256

$fundo   = [System.Drawing.Color]::FromArgb(0x00, 0x00, 0x00)  # azulejo e junta entre os blocos
$corpo   = [System.Drawing.Color]::FromArgb(0x6F, 0xAB, 0x16)  # verde 500, a cor da marca
$encaixe = [System.Drawing.Color]::FromArgb(0x32, 0x4E, 0x09)  # verde 800, as faces dentro do vao

<#
    A marca: um cubo isometrico 2x2x2 de blocos iguais com o bloco de cima a
    direita faltando, e a peca que falta descendo ate o encaixe.

    Cada linha e um quadrilatero num quadro de 100x100: o 1 na frente marca as
    duas faces expostas dentro do vao, que vao no verde fechado. A ordem e a de
    pintura — cada bloco e preenchido e contornado antes do proximo, e e o
    contorno, na cor do fundo, que abre a junta. Trocar a ordem desmonta o cubo.
#>
$marcaCompleta = @(
    @(0, 39.62, 35.98, 56.36, 45.65, 39.62, 55.32, 22.87, 45.65),
    @(0, 56.36, 64.99, 39.62, 74.66, 39.62, 55.32, 56.36, 45.65),
    @(0, 22.87, 64.99, 39.62, 74.66, 39.62, 55.32, 22.87, 45.65),
    @(0, 39.62, 16.64, 56.36, 26.31, 39.62, 35.98, 22.87, 26.31),
    @(1, 56.36, 45.65, 39.62, 55.32, 39.62, 35.98, 56.36, 26.31),
    @(0, 22.87, 45.65, 39.62, 55.32, 39.62, 35.98, 22.87, 26.31),
    @(0, 22.87, 45.65, 39.62, 55.32, 22.87, 64.99, 6.12, 55.32),
    @(0, 39.62, 74.66, 22.87, 84.33, 22.87, 64.99, 39.62, 55.32),
    @(0, 6.12, 74.66, 22.87, 84.33, 22.87, 64.99, 6.12, 55.32),
    @(1, 56.36, 45.65, 73.11, 55.32, 56.36, 64.99, 39.62, 55.32),
    @(0, 73.11, 74.66, 56.36, 84.33, 56.36, 64.99, 73.11, 55.32),
    @(0, 39.62, 74.66, 56.36, 84.33, 56.36, 64.99, 39.62, 55.32),
    @(0, 22.87, 26.31, 39.62, 35.98, 22.87, 45.65, 6.12, 35.98),
    @(0, 39.62, 55.32, 22.87, 64.99, 22.87, 45.65, 39.62, 35.98),
    @(0, 6.12, 55.32, 22.87, 64.99, 22.87, 45.65, 6.12, 35.98),
    @(0, 39.62, 55.32, 56.36, 64.99, 39.62, 74.66, 22.87, 64.99),
    @(0, 56.36, 84.33, 39.62, 94.00, 39.62, 74.66, 56.36, 64.99),
    @(0, 22.87, 84.33, 39.62, 94.00, 39.62, 74.66, 22.87, 64.99),
    @(0, 39.62, 35.98, 56.36, 45.65, 39.62, 55.32, 22.87, 45.65),
    @(0, 56.36, 64.99, 39.62, 74.66, 39.62, 55.32, 56.36, 45.65),
    @(0, 22.87, 64.99, 39.62, 74.66, 39.62, 55.32, 22.87, 45.65),
    @(0, 77.13, 6.00, 93.88, 15.67, 77.13, 25.34, 60.38, 15.67),
    @(0, 93.88, 35.01, 77.13, 44.68, 77.13, 25.34, 93.88, 15.67),
    @(0, 60.38, 35.01, 77.13, 44.68, 77.13, 25.34, 60.38, 15.67)
)

# So o cubo, maior no quadro e sem a peca solta.
$marcaCompacta = @(
    @(0, 50.00, 28.00, 69.05, 39.00, 50.00, 50.00, 30.95, 39.00),
    @(0, 69.05, 61.00, 50.00, 72.00, 50.00, 50.00, 69.05, 39.00),
    @(0, 30.95, 61.00, 50.00, 72.00, 50.00, 50.00, 30.95, 39.00),
    @(0, 50.00, 6.00, 69.05, 17.00, 50.00, 28.00, 30.95, 17.00),
    @(1, 69.05, 39.00, 50.00, 50.00, 50.00, 28.00, 69.05, 17.00),
    @(0, 30.95, 39.00, 50.00, 50.00, 50.00, 28.00, 30.95, 17.00),
    @(0, 30.95, 39.00, 50.00, 50.00, 30.95, 61.00, 11.90, 50.00),
    @(0, 50.00, 72.00, 30.95, 83.00, 30.95, 61.00, 50.00, 50.00),
    @(0, 11.90, 72.00, 30.95, 83.00, 30.95, 61.00, 11.90, 50.00),
    @(1, 69.05, 39.00, 88.10, 50.00, 69.05, 61.00, 50.00, 50.00),
    @(0, 88.10, 72.00, 69.05, 83.00, 69.05, 61.00, 88.10, 50.00),
    @(0, 50.00, 72.00, 69.05, 83.00, 69.05, 61.00, 50.00, 50.00),
    @(0, 30.95, 17.00, 50.00, 28.00, 30.95, 39.00, 11.90, 28.00),
    @(0, 50.00, 50.00, 30.95, 61.00, 30.95, 39.00, 50.00, 28.00),
    @(0, 11.90, 50.00, 30.95, 61.00, 30.95, 39.00, 11.90, 28.00),
    @(0, 50.00, 50.00, 69.05, 61.00, 50.00, 72.00, 30.95, 61.00),
    @(0, 69.05, 83.00, 50.00, 94.00, 50.00, 72.00, 69.05, 61.00),
    @(0, 30.95, 83.00, 50.00, 94.00, 50.00, 72.00, 30.95, 61.00),
    @(0, 50.00, 28.00, 69.05, 39.00, 50.00, 50.00, 30.95, 39.00),
    @(0, 69.05, 61.00, 50.00, 72.00, 50.00, 50.00, 69.05, 39.00),
    @(0, 30.95, 61.00, 50.00, 72.00, 50.00, 50.00, 30.95, 39.00)
)

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

    # O corte e em 32px: acima disso o cubo e a peca solta se distinguem;
    # abaixo, a peca vira um ponto e as juntas fecham.
    if ($s -ge 32) {
        $blocos = $marcaCompleta
        $junta = 1.64   # 8,5% da aresta
    } else {
        $blocos = $marcaCompacta
        $junta = 2.86   # 13% da aresta
    }

    # A marca nao vai de ponta a ponta: com o canto arredondado em 22% do lado,
    # o cubo encostado na borda perde os vertices.
    #
    # Abaixo de 32px a folga encolhe quase a zero. O desenho compacto ja e mais
    # estreito no quadro, e cada pixel que sobra e a diferenca entre ler um cubo
    # e ler um borrao verde: em 16px o bloco tem tres pixels de aresta.
    $ocupacao = $(if ($s -ge 32) { 0.86 } else { 0.98 })
    $escala = $s * $ocupacao / 100.0
    $margem = $s * (1 - $ocupacao) / 2.0

    $caneta = New-Object System.Drawing.Pen $fundo, ([float]($junta * $escala))
    $caneta.LineJoin = 'Round'

    $pincelCorpo = New-Object System.Drawing.SolidBrush $corpo
    $pincelEncaixe = New-Object System.Drawing.SolidBrush $encaixe

    foreach ($bloco in $blocos) {
        $pontos = New-Object 'System.Drawing.PointF[]' 4
        for ($i = 0; $i -lt 4; $i++) {
            $pontos[$i] = New-Object System.Drawing.PointF `
                ([float]($margem + $bloco[1 + $i * 2] * $escala)), ([float]($margem + $bloco[2 + $i * 2] * $escala))
        }

        $g.FillPolygon($(if ($bloco[0] -eq 1) { $pincelEncaixe } else { $pincelCorpo }), $pontos)
        $g.DrawPolygon($caneta, $pontos)
    }

    $pincel.Dispose(); $pincelCorpo.Dispose(); $pincelEncaixe.Dispose()
    $caneta.Dispose(); $caminho.Dispose(); $g.Dispose()
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
