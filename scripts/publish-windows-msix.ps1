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

function Resolve-PackagingRuntimeIdentifier {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Rid
    )

    $trimmed = $Rid.Trim()
    switch -Regex ($trimmed.ToLowerInvariant()) {
        "^win-(x64|x86|arm64)$" {
            $resolved = "win10-$($Matches[1])"
            Write-Warning "RuntimeIdentifier '$trimmed' は MSIX 発行時に不安定なため、'$resolved' に置き換えて続行します。"
            return $resolved
        }
        "^win10-(x64|x86|arm64)$" {
            return $trimmed
        }
        default {
            Write-Warning "RuntimeIdentifier '$trimmed' は未検証です。MSIX 発行では 'win10-x64' / 'win10-x86' / 'win10-arm64' を推奨します。"
            return $trimmed
        }
    }
}

function Get-AppxManifestPublisher {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedProjectPath
    )

    $projectDirectory = Split-Path -Parent $ResolvedProjectPath
    $manifestPath = Join-Path $projectDirectory "Platforms/Windows/Package.appxmanifest"
    if (!(Test-Path $manifestPath)) {
        throw "Package.appxmanifest が見つかりません: $manifestPath"
    }

    [xml]$manifestXml = Get-Content -Path $manifestPath -Raw
    $ns = New-Object System.Xml.XmlNamespaceManager($manifestXml.NameTable)
    $ns.AddNamespace("appx", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $identityNode = $manifestXml.SelectSingleNode("/appx:Package/appx:Identity", $ns)
    if ($null -eq $identityNode) {
        throw "Package.appxmanifest の Identity ノードが見つかりません: $manifestPath"
    }

    $publisher = $identityNode.Attributes["Publisher"]?.Value
    if ([string]::IsNullOrWhiteSpace($publisher)) {
        throw "Package.appxmanifest の Publisher が空です: $manifestPath"
    }

    return $publisher.Trim()
}

function Get-SigningCertificate {
    param(
        [string]$PfxPath,
        [string]$PfxPassword,
        [string]$Thumbprint
    )

    if (![string]::IsNullOrWhiteSpace($PfxPath)) {
        $params = @{
            FilePath = $PfxPath
            NoPromptForPassword = $true
        }

        if (![string]::IsNullOrWhiteSpace($PfxPassword)) {
            $params.Password = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
        }

        return Get-PfxCertificate @params
    }

    $normalizedThumbprint = $Thumbprint.Replace(" ", [string]::Empty).ToUpperInvariant()
    $candidatePaths = @(
        "Cert:\CurrentUser\My\$normalizedThumbprint",
        "Cert:\LocalMachine\My\$normalizedThumbprint"
    )

    foreach ($path in $candidatePaths) {
        $cert = Get-Item -Path $path -ErrorAction SilentlyContinue
        if ($null -ne $cert) {
            return $cert
        }
    }

    throw "指定された証明書サムプリントが見つかりません: $Thumbprint"
}

function Assert-SigningCertificate {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedPublisher
    )

    if (!$Certificate.HasPrivateKey) {
        throw "署名証明書に秘密鍵がありません。PFX（秘密鍵付き）を指定してください。"
    }

    $codeSigningOid = "1.3.6.1.5.5.7.3.3"
    $ekuEntries = $Certificate.EnhancedKeyUsageList | ForEach-Object {
        $oidValue = $null
        if ($_.PSObject.Properties.Name -contains "Value") {
            $oidValue = $_.Value
        }

        if ([string]::IsNullOrWhiteSpace($oidValue) -and ($_.PSObject.Properties.Name -contains "ObjectId")) {
            $oidValue = $_.ObjectId
        }

        if ([string]::IsNullOrWhiteSpace($oidValue) -and ($_.PSObject.Properties.Name -contains "Oid")) {
            $oidValue = $_.Oid.Value
        }

        [pscustomobject]@{
            FriendlyName = $_.FriendlyName
            OidValue = $oidValue
        }
    }

    $hasCodeSigningEku = $ekuEntries `
        | Where-Object {
            $_.OidValue -eq $codeSigningOid `
                -or $_.FriendlyName -eq "Code Signing" `
                -or $_.FriendlyName -eq "コード署名"
        } `
        | Select-Object -First 1

    if ($null -eq $hasCodeSigningEku) {
        $ekuList = $ekuEntries `
            | ForEach-Object { "$($_.FriendlyName) ($($_.OidValue))" }
        $ekuText = if ($null -ne $ekuList -and $ekuList.Count -gt 0) { $ekuList -join ", " } else { "(なし)" }
        throw "証明書に Code Signing EKU (OID: $codeSigningOid) がありません。現在のEKU: $ekuText"
    }

    if (!($Certificate.Subject -eq $ExpectedPublisher)) {
        throw "証明書 Subject と appxmanifest Publisher が不一致です。証明書='$($Certificate.Subject)' / Publisher='$ExpectedPublisher'"
    }
}

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

$resolvedRuntimeIdentifier = Resolve-PackagingRuntimeIdentifier -Rid $RuntimeIdentifier

$args = @(
    "publish",
    $resolvedProjectPath.Path,
    "-f", $Framework,
    "-c", $Configuration,
    "-p:RuntimeIdentifierOverride=$resolvedRuntimeIdentifier",
    "-p:WindowsPackageType=MSIX",
    "-p:GenerateAppxPackageOnBuild=true",
    "-p:UapAppxPackageBuildMode=SideloadOnly",
    "-p:AppxPackageDir=$appxDir\"
)

if ($NoRestore) {
    $args += "--no-restore"
}

if ($enableSigning) {
    $manifestPublisher = Get-AppxManifestPublisher -ResolvedProjectPath $resolvedProjectPath.Path
    $certificate = Get-SigningCertificate -PfxPath $PackageCertificateKeyFile -PfxPassword $PackageCertificatePassword -Thumbprint $PackageCertificateThumbprint
    Assert-SigningCertificate -Certificate $certificate -ExpectedPublisher $manifestPublisher

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
