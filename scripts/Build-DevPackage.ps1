$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist\xbox-dev"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$certArgs = @{
    Type = "CodeSigningCert"
    Subject = "CN=Brimstone Cottage"
    FriendlyName = "Brimstone Xbox Dev Mode"
    CertStoreLocation = "Cert:\CurrentUser\My"
    NotAfter = (Get-Date).AddDays(90)
}

$cert = New-SelfSignedCertificate @certArgs

try {
    $cer = Join-Path $out "Brimstone-Xbox-DevMode.cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null

    $packageDir = Join-Path $out "package\"
    New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

    $buildArgs = @(
        (Join-Path $root "BrimstoneXbox.sln"),
        "/m",
        "/t:Build",
        "/p:Configuration=Release",
        "/p:Platform=x64",
        "/p:AppxPackageSigningEnabled=true",
        "/p:PackageCertificateThumbprint=$($cert.Thumbprint)",
        "/p:GenerateAppxPackageOnBuild=true",
        "/p:AppxBundle=Never",
        "/p:AppxPackageDir=$packageDir"
    )

    & msbuild @buildArgs

    if ($LASTEXITCODE -ne 0) {
        throw "Signed Xbox package build failed with exit code $LASTEXITCODE."
    }

    $packages = Get-ChildItem -Path $packageDir -Recurse -File |
        Where-Object { $_.Extension -in ".appx", ".msix", ".appxbundle", ".msixbundle" }

    if (-not $packages) {
        throw "Build completed but no Xbox app package was produced."
    }

    Write-Host "Xbox Dev Mode package:"
    $packages | ForEach-Object { Write-Host " - $($_.FullName)" }
    Write-Host "Certificate: $cer"
}
finally {
    if ($cert) {
        Remove-Item -Path ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -Force -ErrorAction SilentlyContinue
    }
}
