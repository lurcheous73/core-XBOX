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
        $m = [Math]::Max(2,[int]([Math]::Min($a[1],$a[2])/10))
        $gfx.FillRectangle($accent,$m,$m,$a[1]-2*$m,$a[2]-2*$m)
        $font = [System.Drawing.Font]::new("Segoe UI",[Math]::Max(8,[single]([Math]::Min($a[1],$a[2])*.30)),[System.Drawing.FontStyle]::Bold)
        try {
            $size = $gfx.MeasureString($a[3],$font)
            $gfx.DrawString($a[3],$font,$text,($a[1]-$size.Width)/2,($a[2]-$size.Height)/2)
        } finally { $font.Dispose() }
        $bmp.Save((Join-Path $out $a[0]),[System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $accent.Dispose(); $text.Dispose(); $gfx.Dispose(); $bmp.Dispose()
    }
}
