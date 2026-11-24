param(
    [string]$InnoSetup = 'D:\program_data\python_project\airdrop-plus\installer\InnoSetup',
    [string]$CoreSource,
    [string]$SignTool,
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$SigningThumbprint,
    [switch]$PortableOnly
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $CoreSource) { $CoreSource = Join-Path $root 'artifacts\app\data\core' }
$compiler = Join-Path $InnoSetup 'ISCC.exe'
if (-not $PortableOnly -and -not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "Inno Setup compiler not found: $compiler" }
if (-not $SignTool) {
    $SignTool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $SignTool) {
        $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        $SignTool = Get-ChildItem -LiteralPath $kits -Directory -ErrorAction SilentlyContinue |
            Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (-not $SignTool -or -not (Test-Path -LiteralPath $SignTool -PathType Leaf)) {
    throw 'Microsoft SignTool not found. Install the Windows SDK signing tools or specify -SignTool.'
}
$SignTool = (Resolve-Path -LiteralPath $SignTool).Path
foreach ($file in @('mihomo.exe', 'geoip.metadb', 'geosite.dat')) {
    if (-not (Test-Path -LiteralPath (Join-Path $CoreSource $file) -PathType Leaf)) { throw "Missing required seed file: $file" }
}
$project = [xml](Get-Content -Raw -LiteralPath (Join-Path $root 'src\Xehn.App\Xehn.App.csproj'))
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'The app project does not specify a package version.' }

# Keep the key in the current user's Personal store. Neither building nor
# exporting the public certificate adds it to a Windows trust store.
$subject = 'CN=TrashVPN Local Code Signing'
$friendly = 'TrashVPN local release signing'
$now = Get-Date
$available = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.HasPrivateKey -and $_.NotBefore -le $now -and $_.NotAfter -gt $now }
if ($SigningThumbprint) {
    $certificate = $available | Where-Object Thumbprint -EQ $SigningThumbprint | Select-Object -First 1
    if (-not $certificate) { throw 'The specified code-signing certificate is not usable in Cert:\CurrentUser\My.' }
} else {
    $certificate = $available | Where-Object { $_.Subject -eq $subject -and $_.Issuer -eq $subject -and $_.FriendlyName -eq $friendly } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $certificate) {
        $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -FriendlyName $friendly `
            -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
            -KeyExportPolicy NonExportable -NotAfter $now.AddYears(3)
    }
}

$payload = Join-Path $root ('artifacts\installer-payload\' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $root 'artifacts\installer'
New-Item -ItemType Directory -Force $output | Out-Null
& "$root\build.ps1" -OutputPath $payload -CoreSource $CoreSource -SelfContained
if ($LASTEXITCODE) { throw 'Installer payload build failed.' }

# Sign only our binaries, leaving the SDK and bundled third-party binaries
# unchanged. Inno also signs Setup, its temporary copies, and the uninstaller.
foreach ($file in @('Xehn.App.exe', 'engine\xehn-engine.exe')) {
    & $SignTool sign /s My /sha1 $certificate.Thumbprint /fd SHA256 /d TrashVPN (Join-Path $payload $file)
    if ($LASTEXITCODE) { throw "Signing failed: $file" }
}

# The fresh, signed payload is also the portable layout. Only Inno Setup
# adds installed.mode and moves the seed files out of data\core.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$portableName = "TrashVPN-$version-Windows-x64-Portable.zip"
$stagedZip = $payload + '.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $stagedZip, [IO.Compression.CompressionLevel]::Optimal, $false)

if (-not $PortableOnly) {
    $signedCache = Join-Path $root ('.cache\installer-uninstallers\' + $certificate.Thumbprint)
    New-Item -ItemType Directory -Force $signedCache | Out-Null
    $signer = '/Strashvpn=$q' + $SignTool + '$q sign /s My /sha1 ' + $certificate.Thumbprint + ' /fd SHA256 /d TrashVPN $f'
    & $compiler $signer "/DPayload=$payload" "/DAppVersion=$version" "/DSignedCache=$signedCache" "/O$output" "$root\installer\TrashVPN.iss"
    if ($LASTEXITCODE) { throw 'Inno Setup compilation failed.' }
    $setup = Get-Item -LiteralPath (Join-Path $output "TrashVPN-$version-Windows-x64-Signed-Setup.exe")
    $signature = Get-AuthenticodeSignature -LiteralPath $setup.FullName
    if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or $signature.Status -eq 'HashMismatch') {
        throw 'The installer does not contain the expected intact Authenticode signature.'
    }
}
Export-Certificate -Cert $certificate -FilePath (Join-Path $output "TrashVPN-CodeSigning-$($certificate.Thumbprint).cer") -Type CERT -Force | Out-Null
$portablePath = Join-Path $output $portableName
[IO.File]::Copy($stagedZip, $portablePath, $true)
if (-not $PortableOnly) { $setup }
Get-Item -LiteralPath $portablePath
