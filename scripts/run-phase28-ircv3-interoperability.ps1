[CmdletBinding()]
param(
    [ValidateSet("ergo-testnet", "inspircd-local-full", "inspircd-local-noecho", "inspircd-local-nomsgid", "inspircd-local-restricted-tags", "inspircd-local-no-tags", "inspircd-local-no-message-tags", "inspircd-local-no-server-time")]
    [string]$Profile = "ergo-testnet",
    [string]$Server,
    [ValidateRange(1, 65535)]
    [int]$Port,
    [switch]$NoTls,
    [switch]$Tls,
    [ValidateRange(15, 600)]
    [int]$TimeoutSeconds = 90,
    [string]$OutputPath,
    [string]$TranscriptPath
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\nexIRC.Interoperability\nexIRC.Interoperability.csproj"
$artifactRoot = Join-Path $repoRoot "artifacts\phase28"
$OutputPath = if ([string]::IsNullOrWhiteSpace($OutputPath)) { Join-Path $artifactRoot ("{0}-result.json" -f $Profile) } else { $OutputPath }
$TranscriptPath = if ([string]::IsNullOrWhiteSpace($TranscriptPath)) { Join-Path $artifactRoot ("{0}-transcript.jsonl" -f $Profile) } else { $TranscriptPath }

$arguments = @("run", "--project", $projectPath, "--no-restore", "--")
$arguments += @("--profile", $Profile)
if (-not [string]::IsNullOrWhiteSpace($Server)) { $arguments += @("--server", $Server) }
if ($Port -gt 0) { $arguments += @("--port", $Port.ToString([Globalization.CultureInfo]::InvariantCulture)) }
if ($NoTls) { $arguments += "--no-tls" }
elseif ($Tls) { $arguments += "--tls" }
$arguments += @(
    "--timeout-seconds", $TimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--output", $OutputPath,
    "--transcript", $TranscriptPath
)

& dotnet @arguments
exit $LASTEXITCODE
