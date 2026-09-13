[CmdletBinding()]
param(
    [ValidateRange(15, 600)]
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\nexIRC.Interoperability\nexIRC.Interoperability.csproj"
$artifactRoot = Join-Path $repoRoot "artifacts\phase29"
$resultPath = Join-Path $artifactRoot "inspircd-testnet-result.json"
$transcriptPath = Join-Path $artifactRoot "inspircd-testnet-transcript.jsonl"
$comparisonPath = Join-Path $artifactRoot "server-comparison.json"

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Phase 29 interoperability project was not found: $projectPath"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The dotnet SDK is required to run the Phase 29 interoperability harness."
}

$null = New-Item -ItemType Directory -Path $artifactRoot -Force
$arguments = @(
    "run", "--project", $projectPath, "--no-restore", "--",
    "--profile", "inspircd-testnet",
    "--timeout-seconds", $TimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--output", $resultPath,
    "--transcript", $transcriptPath
)

$harnessExitCode = 1
Push-Location $repoRoot
try {
    & dotnet @arguments
    $harnessExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

function Get-ScenarioStatus {
    param(
        [object]$Document,
        [string]$Name
    )

    $classification = @($Document.Scenarios.Classifications | Where-Object { $_.Name -eq $Name } | Select-Object -First 1)
    if ($classification.Count -eq 0) {
        switch ($Name) {
            "canonical-parent" { if ($Document.Scenarios.ChannelParent) { return "Passed" }; return "not-proven" }
            "reply-relay" { if ($Document.Scenarios.ReplyRelay) { return "Passed" }; return "not-proven" }
            "reaction-lifecycle" { if ($Document.Scenarios.ReactionRelay) { return "Passed" }; return "not-proven" }
            "unreaction" { if ($Document.Scenarios.UnreactionRelay) { return "Passed" }; return "not-proven" }
            "reconnect" { if ($Document.Scenarios.Reconnect) { return "Passed" }; return "not-proven" }
            "query-reply-reaction" { if ($Document.Scenarios.QueryReply -and $Document.Scenarios.QueryReaction) { return "Passed" }; return "not-proven" }
            "durable-reopen" { if ($Document.Scenarios.DurableEvidence) { return "Passed" }; return "not-proven" }
            default { return "unobserved" }
        }
    }
    return [string]$classification[0].Status
}

function Get-CapabilityState {
    param(
        [object]$Server,
        [string]$Capability
    )

    $matrixProperty = $Server.CapabilityMatrix.PSObject.Properties | Where-Object { $_.Name -eq $Capability } | Select-Object -First 1
    if ($null -ne $matrixProperty) { return [string]$matrixProperty.Value }
    if (@($Server.EnabledCapabilities) -contains $Capability) { return "enabled" }
    if (@($Server.AdvertisedCapabilities) -contains $Capability) { return "advertised-not-enabled" }
    return "absent"
}

function Get-HistoricalReactionState {
    param([object]$Document)

    $classification = @($Document.Scenarios.Classifications | Where-Object { $_.Name -eq "chathistory-recovery" } | Select-Object -First 1)
    if ($classification.Count -eq 0) {
        if ($Document.Server.History.CapabilityEnabled -and $Document.Server.History.EventPlaybackEnabled) { return "observed" }
        if (-not $Document.Server.History.CapabilityEnabled) { return "unsupported" }
        return "not-proven"
    }
    if ([string]$classification[0].Detail -match "reaction=present") { return "observed" }
    if ([string]$classification[0].Status -eq "Unsupported") { return "unsupported" }
    return [string]$classification[0].Status
}

if (Test-Path -LiteralPath $resultPath) {
    $inspircd = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    $ergoPath = Join-Path $repoRoot "artifacts\phase27\live-result.json"
    $ergo = if (Test-Path -LiteralPath $ergoPath) { Get-Content -Raw -LiteralPath $ergoPath | ConvertFrom-Json } else { $null }

    $rows = @(
        [ordered]@{ Property = "Server/version"; Ergo = if ($null -eq $ergo) { "unavailable" } else { "$($ergo.Server.ProbableIrcd ?? 'Ergo') / $($ergo.Server.Version)" }; InspIRCdTestnet = "$($inspircd.Server.ServerName ?? 'unknown') / $($inspircd.Server.Version)" }
        [ordered]@{ Property = "message-tags"; Ergo = if ($null -eq $ergo) { "unavailable" } else { Get-CapabilityState $ergo.Server "message-tags" }; InspIRCdTestnet = Get-CapabilityState $inspircd.Server "message-tags" }
        [ordered]@{ Property = "echo-message"; Ergo = if ($null -eq $ergo) { "unavailable" } else { Get-CapabilityState $ergo.Server "echo-message" }; InspIRCdTestnet = Get-CapabilityState $inspircd.Server "echo-message" }
        [ordered]@{ Property = "msgid"; Ergo = if ($null -eq $ergo) { "unavailable" } elseif ($ergo.Scenarios.CanonicalParentMessageId) { "observed" } else { "not-observed" }; InspIRCdTestnet = if ($inspircd.Scenarios.CanonicalParentMessageId) { "observed" } else { "not-observed" } }
        [ordered]@{ Property = "server-time"; Ergo = if ($null -eq $ergo) { "unavailable" } else { Get-CapabilityState $ergo.Server "server-time" }; InspIRCdTestnet = Get-CapabilityState $inspircd.Server "server-time" }
        [ordered]@{ Property = "account-tag"; Ergo = if ($null -eq $ergo) { "unavailable" } else { Get-CapabilityState $ergo.Server "account-tag" }; InspIRCdTestnet = Get-CapabilityState $inspircd.Server "account-tag" }
        [ordered]@{ Property = "batch"; Ergo = if ($null -eq $ergo) { "unavailable" } else { Get-CapabilityState $ergo.Server "batch" }; InspIRCdTestnet = Get-CapabilityState $inspircd.Server "batch" }
        [ordered]@{ Property = "labeled-response"; Ergo = if ($null -eq $ergo) { "unavailable" } else { Get-CapabilityState $ergo.Server "labeled-response" }; InspIRCdTestnet = Get-CapabilityState $inspircd.Server "labeled-response" }
        [ordered]@{ Property = "CHATHISTORY"; Ergo = if ($null -eq $ergo) { "unavailable" } elseif ($ergo.Server.History.CapabilityEnabled) { "enabled" } else { "unsupported" }; InspIRCdTestnet = if ($inspircd.Server.History.CapabilityEnabled) { "enabled" } else { "unsupported" } }
        [ordered]@{ Property = "event playback"; Ergo = if ($null -eq $ergo) { "unavailable" } elseif ($ergo.Server.History.EventPlaybackEnabled) { "enabled" } else { "unsupported" }; InspIRCdTestnet = if ($inspircd.Server.History.EventPlaybackEnabled) { "enabled" } else { "unsupported" } }
        [ordered]@{ Property = "CLIENTTAGDENY"; Ergo = if ($null -eq $ergo) { "unavailable" } else { [string]$ergo.Server.ClientTagDeny }; InspIRCdTestnet = [string]$inspircd.Server.ClientTagDeny }
        [ordered]@{ Property = "Unknown client tag relay"; Ergo = if ($null -eq $ergo) { "unavailable" } else { Get-ScenarioStatus $ergo "unknown-client-tag-relay" }; InspIRCdTestnet = Get-ScenarioStatus $inspircd "unknown-client-tag-relay" }
        [ordered]@{ Property = "Reply relay"; Ergo = if ($null -eq $ergo) { "unavailable" } else { Get-ScenarioStatus $ergo "reply-relay" }; InspIRCdTestnet = Get-ScenarioStatus $inspircd "reply-relay" }
        [ordered]@{ Property = "Reaction relay"; Ergo = if ($null -eq $ergo) { "unavailable" } else { Get-ScenarioStatus $ergo "reaction-lifecycle" }; InspIRCdTestnet = Get-ScenarioStatus $inspircd "reaction-lifecycle" }
        [ordered]@{ Property = "Unreaction relay"; Ergo = if ($null -eq $ergo) { "unavailable" } else { Get-ScenarioStatus $ergo "unreaction" }; InspIRCdTestnet = Get-ScenarioStatus $inspircd "unreaction" }
        [ordered]@{ Property = "Echo dedup"; Ergo = if ($null -eq $ergo) { "unavailable" } elseif ($ergo.Scenarios.ReactionEcho -and $ergo.Scenarios.ReplyRelay) { "passed" } else { "not-proven" }; InspIRCdTestnet = if ($inspircd.Scenarios.ReactionEcho -and $inspircd.Scenarios.ReplyRelay) { "passed" } else { "not-proven" } }
        [ordered]@{ Property = "Query relationships"; Ergo = if ($null -eq $ergo) { "unavailable" } elseif ($ergo.Scenarios.QueryReply -and $ergo.Scenarios.QueryReaction) { "passed" } else { "not-proven" }; InspIRCdTestnet = if ($inspircd.Scenarios.QueryReply -and $inspircd.Scenarios.QueryReaction) { "passed" } else { "not-proven" } }
        [ordered]@{ Property = "Reconnect"; Ergo = if ($null -eq $ergo) { "unavailable" } else { Get-ScenarioStatus $ergo "reconnect" }; InspIRCdTestnet = Get-ScenarioStatus $inspircd "reconnect" }
        [ordered]@{ Property = "Historical reactions"; Ergo = if ($null -eq $ergo) { "unavailable" } else { Get-HistoricalReactionState $ergo }; InspIRCdTestnet = Get-HistoricalReactionState $inspircd }
    )

    $comparison = [ordered]@{
        SchemaVersion = "nexIRC-phase29-comparison-v1"
        Outcome = [string]$inspircd.Outcome
        ErgoEvidence = $ergoPath
        InspIRCdEvidence = $resultPath
        Properties = $rows
        Note = "Comparison is semantic; raw server tag ordering is not compared."
    }
    $comparison | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $comparisonPath -Encoding utf8
}

$finalOutcome = "TESTNET_UNREACHABLE"
if (Test-Path -LiteralPath $resultPath) {
    $finalOutcome = [string](Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json).Outcome
}
Write-Host ("Phase 29 InspIRCd testnet outcome: {0}" -f $finalOutcome)
Write-Host ("Result: {0}" -f $resultPath)
Write-Host ("Transcript: {0}" -f $transcriptPath)
Write-Host ("Comparison: {0}" -f $comparisonPath)
exit $harnessExitCode
