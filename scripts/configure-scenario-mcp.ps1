[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataDirectory,
    [ValidateSet('Enable', 'Rotate', 'Disable')][string]$Action = 'Enable',
    [ValidateRange(1, 65535)][int]$Port = 47831
)
$ErrorActionPreference = 'Stop'
try {
if (-not [IO.Path]::IsPathFullyQualified($DataDirectory)) { throw 'DataDirectory must be absolute.' }
$configPath = Join-Path ([IO.Path]::GetFullPath($DataDirectory)) 'scenario-mcp.json'
if ($Action -eq 'Enable' -and (Test-Path -LiteralPath $configPath)) {
    throw 'Configuration already exists. Use Rotate explicitly to replace both keys, or edit Enabled while the app is stopped.'
}
if ($Action -eq 'Disable') {
    if (-not (Test-Path -LiteralPath $configPath)) { throw 'No MCP configuration exists.' }
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $config.Enabled = $false
} else {
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $structureBytes = New-Object byte[] 32
        $fullBytes = New-Object byte[] 32
        $generator.GetBytes($structureBytes)
        $generator.GetBytes($fullBytes)
        $config = [ordered]@{
            Enabled = $true
            Port = $Port
            StructureKey = ([BitConverter]::ToString($structureBytes)).Replace('-', '').ToLowerInvariant()
            FullKey = ([BitConverter]::ToString($fullBytes)).Replace('-', '').ToLowerInvariant()
        }
    } finally { $generator.Dispose() }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($configPath)) | Out-Null
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
Write-Output 'MCP configuration saved. Restart GameLauncher to apply. Keys were not printed.'
} catch {
    throw 'MCP configuration could not be updated. Check the absolute data directory, action, and existing configuration while the app is stopped.'
}
