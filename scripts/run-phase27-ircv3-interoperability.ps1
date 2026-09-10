param(
    [string]$Server = "testnet.ergo.chat",
    [ValidateRange(1, 65535)]
    [int]$Port = 6697,
    [switch]$NoTls,
    [ValidateRange(15, 600)]
    [int]$TimeoutSeconds = 120,
    [string]$OutputPath,
    [string]$TranscriptPath
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\nexIRC.Interoperability\nexIRC.Interoperability.csproj"
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Phase 27 interoperability project was not found: $projectPath"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The dotnet SDK is required to run the Phase 27 interoperability harness."
}

if ($NoTls -and $Server -notin @("localhost", "127.0.0.1", "::1")) {
    throw "Plaintext Phase 27 runs are restricted to localhost. Use TLS for a remote test service."
}

$artifactDirectory = Join-Path $repoRoot "artifacts\phase27"
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $artifactDirectory "phase27-result-$stamp.json"
} elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repoRoot $OutputPath
}

if ([string]::IsNullOrWhiteSpace($TranscriptPath)) {
    $TranscriptPath = [System.IO.Path]::ChangeExtension($OutputPath, ".transcript.jsonl")
} elseif (-not [System.IO.Path]::IsPathRooted($TranscriptPath)) {
    $TranscriptPath = Join-Path $repoRoot $TranscriptPath
}

$tlsArgument = if ($NoTls) { "--no-tls" } else { "--tls" }
$arguments = @(
    "run",
    "--project", $projectPath,
    "--no-restore",
    "--",
    "--server", $Server,
    "--port", $Port.ToString([Globalization.CultureInfo]::InvariantCulture),
    $tlsArgument,
    "--timeout-seconds", $TimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--output", $OutputPath,
    "--transcript", $TranscriptPath
)

Push-Location $repoRoot
try {
    & dotnet @arguments
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
