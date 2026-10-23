$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "BrimstoneXbox\Assets"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$assets = @(
    @("LockScreenLogo.scale-200.png",48,48,"B"),
    @("SplashScreen.scale-200.png",1240,600,"BRIMSTONE"),
    @("Square44x44Logo.scale-200.png",88,88,"B"),
    @("Square44x44Logo.targetsize-24_altform-unplated.png",24,24,"B"),
    @("Square150x150Logo.scale-200.png",300,300,"B"),
    @("StoreLogo.png",50,50,"B"),
    @("Wide310x150Logo.scale-200.png",620,300,"BRIMSTONE")
)

foreach ($a in $assets) {
    $bmp = [System.Drawing.Bitmap]::new($a[1], $a[2])
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $accent = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(143,91,255))
    $text = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(243,241,248))
    try {
        $gfx.Clear([System.Drawing.Color]::FromArgb(8,8,11))
        $brand = [System.Drawing.Image]::FromFile((Join-Path $out "Brand.png"))
        try {
            $size = [Math]::Min($a[1], $a[2])
            $gfx.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $gfx.DrawImage($brand, [int](($a[1]-$size)/2), [int](($a[2]-$size)/2), $size, $size)
        } finally { $brand.Dispose() }
        $bmp.Save((Join-Path $out $a[0]),[System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $accent.Dispose(); $text.Dispose(); $gfx.Dispose(); $bmp.Dispose()
    }
}
