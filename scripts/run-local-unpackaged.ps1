param(
    [string]$ProjectPath = "src/GameLauncherWithGit/GameLauncherWithGit.csproj",
    [string]$Framework = "net9.0-windows10.0.19041.0",
    [string]$Configuration = "Debug",
    [switch]$BuildOnly,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$resolvedProjectPath = Resolve-Path -Path $ProjectPath

if ($BuildOnly) {
    $args = @(
        "build",
        $resolvedProjectPath.Path,
        "-f", $Framework,
        "-c", $Configuration,
        "-p:WindowsPackageType=None"
    )
}
else {
    $args = @(
        "run",
        "--project", $resolvedProjectPath.Path,
        "-f", $Framework,
        "-c", $Configuration,
        "-p:WindowsPackageType=None"
    )
}

if ($NoRestore) {
    $args += "--no-restore"
}

if ($BuildOnly) {
    Write-Host "ローカル実行向けビルド (Unpackaged) を開始します..."
}
else {
    Write-Host "ローカル実行 (Unpackaged) を開始します..."
}
Write-Host "dotnet $($args -join ' ')"

& dotnet @args

if ($LASTEXITCODE -ne 0) {
    throw "ローカル実行に失敗しました。"
}
