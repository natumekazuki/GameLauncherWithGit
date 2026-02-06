param(
    [string]$ProjectPath = "src/GameLauncherWithGit/GameLauncherWithGit.csproj",
    [string]$Framework = "net9.0-windows10.0.19041.0",
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win10-x64",
    [string]$OutputRoot = "artifacts/msix",
    [string]$PackageCertificateThumbprint = "",
    [string]$PackageCertificateKeyFile = "",
    [string]$PackageCertificatePassword = "",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$resolvedProjectPath = Resolve-Path -Path $ProjectPath
$resolvedOutputRoot = Join-Path (Resolve-Path ".").Path $OutputRoot
New-Item -ItemType Directory -Force -Path $resolvedOutputRoot | Out-Null

$appxDir = Join-Path $resolvedOutputRoot "AppPackages"
New-Item -ItemType Directory -Force -Path $appxDir | Out-Null

$enableSigning = $false
if (![string]::IsNullOrWhiteSpace($PackageCertificateThumbprint)) {
    $enableSigning = $true
}

if (![string]::IsNullOrWhiteSpace($PackageCertificateKeyFile)) {
    $enableSigning = $true
}

if ([string]::IsNullOrWhiteSpace($PackageCertificatePassword) -and $env:MSIX_CERT_PASSWORD) {
    $PackageCertificatePassword = $env:MSIX_CERT_PASSWORD
}

$args = @(
    "publish",
    $resolvedProjectPath.Path,
    "-f", $Framework,
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "-p:TargetFrameworks=$Framework",
    "-p:WindowsPackageType=MSIX",
    "-p:GenerateAppxPackageOnBuild=true",
    "-p:UapAppxPackageBuildMode=SideloadOnly",
    "-p:AppxPackageDir=$appxDir\"
)

if ($NoRestore) {
    $args += "--no-restore"
}

if ($enableSigning) {
    $args += "-p:AppxPackageSigningEnabled=true"
    if (![string]::IsNullOrWhiteSpace($PackageCertificateThumbprint)) {
        $args += "-p:PackageCertificateThumbprint=$PackageCertificateThumbprint"
    }

    if (![string]::IsNullOrWhiteSpace($PackageCertificateKeyFile)) {
        $resolvedPfxPath = Resolve-Path -Path $PackageCertificateKeyFile
        $args += "-p:PackageCertificateKeyFile=$($resolvedPfxPath.Path)"
        if (![string]::IsNullOrWhiteSpace($PackageCertificatePassword)) {
            $args += "-p:PackageCertificatePassword=$PackageCertificatePassword"
        }
    }
}
else {
    $args += "-p:AppxPackageSigningEnabled=false"
}

Write-Host "MSIX 発行を開始します..."
Write-Host "dotnet $($args -join ' ')"

& dotnet @args

if ($LASTEXITCODE -ne 0) {
    throw "MSIX 発行に失敗しました。"
}

Write-Host "完了: $appxDir"
