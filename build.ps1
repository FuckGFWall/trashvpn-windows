param([string]$CoreSource, [string]$GoExecutable)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $GoExecutable) {
    $privateGo = Join-Path $root '.cache\toolchains\go1.26.3\go\bin\go.exe'
    $GoExecutable = if (Test-Path -LiteralPath $privateGo) { $privateGo } else { 'go' }
}
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
$output = Join-Path $root 'artifacts\app'
# Do not replace binaries that belong to a running client (including its core).
$running = Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw '构建目录中的客户端或内核仍在运行。请先自行退出客户端，再运行 build.ps1；不会自动停止进程。' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:GOCACHE = Join-Path $root '.cache\go-build'
$env:GOMODCACHE = Join-Path $root '.cache\go-mod'
New-Item -ItemType Directory -Force $output,"$output\engine",$env:GOCACHE,$env:GOMODCACHE | Out-Null
& $dotnet build "$root\src\Xehn.App\Xehn.App.csproj" -c Release -o $output --nologo
if ($LASTEXITCODE) { throw 'WPF build failed' }
Push-Location "$root\engine"
try {
    & $GoExecutable build -trimpath -ldflags '-s -w -H windowsgui -X main.version=fluent-0.1 -X main.windowsGUIBuild=1' -o "$output\engine\xehn-engine.exe" .
    if ($LASTEXITCODE) { throw 'Engine build failed' }
} finally { Pop-Location }
Copy-Item "$root\engine\THIRD_PARTY_NOTICES.md" $output
New-Item -ItemType Directory -Force "$output\licenses" | Out-Null
Get-ChildItem -LiteralPath "$root\engine\third_party" | Copy-Item -Destination "$output\licenses" -Recurse -Force
if ($CoreSource) {
    $core = Join-Path $output 'data\core'
    New-Item -ItemType Directory -Force $core | Out-Null
    # Allowlist only: never import an account, active state, settings, or generated config.
    foreach ($name in @('mihomo.exe', 'geoip.metadb', 'geosite.dat')) {
        $source = Join-Path $CoreSource $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $core $name) }
    }
}
Write-Host "Built: $output\Xehn.App.exe (no packaging or installation performed)"
