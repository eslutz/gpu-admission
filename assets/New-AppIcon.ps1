# Build a multi-resolution Windows icon from the app's geometric GPU mark.
param([string]$Output = (Join-Path $PSScriptRoot 'GpuAdmission.ico'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
function New-Brush([string]$Hex) { [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($Hex)) }
$sizes=16,20,24,32,40,48,64,128,256
$frames=@()
foreach($size in $sizes){
    $bmp=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g=[Drawing.Graphics]::FromImage($bmp)
    $ink=New-Brush '#15243A'; $accent=New-Brush '#78E9DF'
    $shape=[Drawing.Drawing2D.GraphicsPath]::new()
    $stream=[IO.MemoryStream]::new()
    try {
        $g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $scale=[single]($size / 64.0)
        $g.ScaleTransform($scale,$scale)
        $shape.AddArc(2,2,24,24,180,90); $shape.AddArc(38,2,24,24,270,90)
        $shape.AddArc(38,38,24,24,0,90); $shape.AddArc(2,38,24,24,90,90)
        $shape.CloseFigure()
        $g.FillPath($ink,$shape)
        $g.FillRectangle($accent,15,17,34,30)
        $g.FillRectangle($ink,20,22,24,20)
        foreach($x in 20,29,38){
            $g.FillRectangle($accent,$x,11,6,6)
            $g.FillRectangle($accent,$x,47,6,6)
        }
        $bolt=[Drawing.Point[]]@(
            [Drawing.Point]::new(33,23),[Drawing.Point]::new(25,34),
            [Drawing.Point]::new(31,34),[Drawing.Point]::new(30,41),
            [Drawing.Point]::new(39,29),[Drawing.Point]::new(33,29))
        $g.FillPolygon($accent,$bolt)
        $bmp.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
        $frames+= ,$stream.ToArray()
        if($size -eq 256){[IO.File]::WriteAllBytes((Join-Path (Split-Path $Output) 'GpuAdmission.png'),$frames[-1])}
    }finally{$stream.Dispose();$shape.Dispose();$accent.Dispose();$ink.Dispose();$g.Dispose();$bmp.Dispose()}
}
$file=[IO.File]::Create($Output)
$writer=[IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++){
        $side=[byte]($sizes[$i] % 256)
        $writer.Write($side);$writer.Write($side);$writer.Write([uint16]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)
        $offset+=$frames[$i].Length
    }
    foreach($frame in $frames){$writer.Write([byte[]]$frame)}
}finally{$writer.Dispose()}
