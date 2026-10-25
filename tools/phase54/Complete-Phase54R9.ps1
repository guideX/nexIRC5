[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\phase54r9'))
$runDirectory = [System.IO.Path]::GetFullPath($RunDirectory)
$prefix = $artifactRoot.TrimEnd('\') + '\'
if (-not $runDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Run directory must be inside $artifactRoot."
}
$sessionPath = Join-Path $runDirectory 'session.json'
if (-not (Test-Path -LiteralPath $sessionPath -PathType Leaf)) { throw "Run manifest not found: $sessionPath" }
$session = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json

function Get-RepositorySnapshot {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return [pscustomobject]@{ Path = $Path; Available = $false; Branch = 'unknown'; Head = 'unknown'; Worktree = 'unknown'; TrackingDivergence = 'unknown' }
    }
    $branch = (& git -C $Path branch --show-current 2>$null | Select-Object -First 1)
    $head = (& git -C $Path rev-parse HEAD 2>$null | Select-Object -First 1)
    $status = @(& git -C $Path status --porcelain=v1 2>$null)
    $upstream = (& git -C $Path rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null | Select-Object -First 1)
    $divergence = 'no upstream'
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($upstream)) {
        $counts = (& git -C $Path rev-list --left-right --count "HEAD...$upstream" 2>$null | Select-Object -First 1)
        if ($LASTEXITCODE -eq 0) { $divergence = ($counts -replace '\s+', ' ').Trim() }
    }
    return [pscustomobject]@{
        Available = $true
        Branch = if ($branch) { $branch.Trim() } else { 'unknown' }
        Head = if ($head) { $head.Trim() } else { 'unknown' }
        Worktree = if ($status.Count -eq 0) { 'clean' } else { 'dirty' }
        TrackingDivergence = $divergence
    }
}

function Get-RepositorySummary {
    param([object]$Snapshot)
    return [pscustomobject]@{
        Available = [bool]$Snapshot.Available
        Branch = $Snapshot.Branch
        Head = $Snapshot.Head
        Worktree = $Snapshot.Worktree
        TrackingDivergence = $Snapshot.TrackingDivergence
    }
}

function Get-BaselineValues {
    param([object[]]$Records, [string]$Stage, [string]$Client)
    $record = $Records | Where-Object { $_.RecordType -eq 'baseline' -and $_.Stage -eq $Stage -and $_.Client -eq $Client } | Select-Object -Last 1
    if ($record) { return $record.Values }
    return [ordered]@{ Status = 'no baseline fingerprints recorded' }
}

function Format-BaselineValue {
    param([object]$Value)
    if ([string]::IsNullOrWhiteSpace([string]$Value)) { return 'not recorded' }
    return [string]$Value
}

function Get-CheckpointJournal {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $records.Add(($line | ConvertFrom-Json)) } catch { }
    }
    return $records.ToArray()
}

function Sanitize-CommentValue {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    $safe = ($Value -replace '[\x00-\x1F\x7F]', ' ').Trim()
    $safe = $safe -replace '(?i)\b(password|passwd|token|secret|credential|authorization)\s*[:=]\s*\S+', '$1=[REDACTED]'
    $safe = $safe -replace '(?i)\bbearer\s+\S+', 'Bearer [REDACTED]'
    $safe = $safe -replace '(?i)\b(sasl|resume)\s*[:=]\s*\S+', '$1=[REDACTED]'
    if ($safe.Length -gt 200) { $safe = $safe.Substring(0, 200) }
    return $safe
}

function Get-TelemetryCategories {
    param([object]$Record)
    if ($null -eq $Record.TelemetryLineRange -or [string]::IsNullOrWhiteSpace($Record.TelemetryPath)) { return @() }
    if (-not (Test-Path -LiteralPath $Record.TelemetryPath -PathType Leaf)) { return @() }
    $range = @($Record.TelemetryLineRange)
    if ($range.Count -ne 2) { return @() }
    try { $lines = [System.IO.File]::ReadAllLines($Record.TelemetryPath) } catch { return @() }
    $start = [Math]::Max(0, [int]$range[0])
    $count = [Math]::Max(0, [Math]::Min($lines.Length - $start, ([int]$range[1] - [int]$range[0])))
    if ($count -eq 0) { return @() }
    $counts = [ordered]@{}
    foreach ($line in $lines[$start..($start + $count - 1)]) {
        $category = $null
        if ($line.StartsWith('APP_CONTEXT_MENU_ACTION ', [StringComparison]::Ordinal)) { $category = 'APP_CONTEXT_MENU_ACTION' }
        elseif ($line.StartsWith('APP_PIN_PROJECTION ', [StringComparison]::Ordinal)) { $category = 'APP_PIN_PROJECTION' }
        elseif ($line.StartsWith('APP_PINNED_ACTIVATION ', [StringComparison]::Ordinal)) { $category = 'APP_PINNED_ACTIVATION' }
        elseif ($line.StartsWith('APP_VIEW_STATE ', [StringComparison]::Ordinal)) { $category = 'APP_VIEW_STATE' }
        elseif ($line.StartsWith('WIN32_MESSAGE ', [StringComparison]::Ordinal)) { $category = 'WIN32_MESSAGE' }
        elseif ($line -match '^(WPF_[A-Z_]+)') { $category = $Matches[1] }
        elseif ($line -match '^(APP_[A-Z_]+)') { $category = $Matches[1] }
        if ($category) { $counts[$category] = [int]$counts[$category] + 1 }
    }
    return @($counts.GetEnumerator() | ForEach-Object { [pscustomobject]@{ Category = $_.Key; Count = $_.Value } })
}

function Get-ResultForGate {
    param([string]$Id, [object[]]$Records, [object[]]$RunnerSteps)
    $record = $Records | Where-Object { $_.RecordType -eq 'checkpoint' -and $_.Id -eq $Id } | Select-Object -Last 1
    if ($null -ne $record) {
        return [pscustomobject]@{
            Id = $Id
            Result = $record.Result
            Required = [bool]$record.Required
            EvidenceProvenance = $record.EvidenceProvenance
            Comment = if ([string]::IsNullOrWhiteSpace([string]$record.Comment)) { '' } else { 'Operator note omitted from the shareable report; review the local journal if needed.' }
            TelemetryCategories = @(Get-TelemetryCategories -Record $record)
        }
    }
    $runner = $RunnerSteps | Where-Object { $_.ScenarioId -eq $Id } | Select-Object -Last 1
    if ($null -ne $runner) {
        $result = if ($runner.Result -eq 'PASS') { 'PASS' } elseif ($runner.Result -eq 'FAIL') { 'FAIL' } elseif ($runner.Result -eq 'BLOCKED') { 'BLOCKED' } else { 'UNVERIFIED' }
        return [pscustomobject]@{
            Id = $Id
            Result = $result
            Required = $true
            EvidenceProvenance = if ($runner.Result -in @('PASS', 'FAIL')) { 'automated observation' } else { 'no evidence' }
            Comment = 'Existing attached UI acceptance runner result. Its details are retained locally and omitted from this sanitized report.'
            TelemetryCategories = @()
            RunnerEvidenceLevel = $runner.EvidenceLevel
        }
    }
    return [pscustomobject]@{ Id = $Id; Result = 'UNVERIFIED'; Required = $true; EvidenceProvenance = 'no evidence'; Comment = 'No result was recorded for this required gate.'; TelemetryCategories = @() }
}

function Get-ClientIdentitySummary {
    param([object]$Client)
    $items = [System.Collections.Generic.List[object]]::new()
    if ($null -ne $Client.Initial) {
        $items.Add([pscustomobject]@{ Role = 'initial'; ProcessId = $Client.Initial.ProcessId; Executable = [System.IO.Path]::GetFileName($Client.Initial.ExecutablePath); SessionId = $Client.Initial.SessionId; WindowVerified = [bool]$Client.Initial.WindowVerified; Reused = [bool]$Client.Reused })
    }
    if ($null -ne $Client.Reopened) {
        $items.Add([pscustomobject]@{ Role = 'reopened'; ProcessId = $Client.Reopened.ProcessId; Executable = [System.IO.Path]::GetFileName($Client.Reopened.ExecutablePath); SessionId = $Client.Reopened.SessionId; WindowVerified = [bool]$Client.Reopened.WindowVerified; Reused = $false })
    }
    return $items.ToArray()
}

$records = @(Get-CheckpointJournal -Path (Join-Path $runDirectory 'operator-checkpoints.jsonl'))
$runnerSteps = @()
$runnerReportExists = $false
if (Test-Path -LiteralPath $session.RunnerReportPath -PathType Leaf) {
    try {
        $runnerJson = Get-Content -LiteralPath $session.RunnerReportPath -Raw | ConvertFrom-Json
        $runnerSteps = @($runnerJson.Steps)
        $runnerReportExists = $true
    } catch { $runnerSteps = @(); $runnerReportExists = $false }
}
$machineRecords = @($records | Where-Object { $_.RecordType -eq 'machine-check' })

$gateIds = @(
    'CLIENT-PAIR', 'SERVER-IDENTITY', 'AUTH-SAME-ACCOUNT', 'CAPABILITY', 'INITIAL-CONVERGENCE',
    'CONTROLLED-FIXTURE', 'BASELINE-A', 'BASELINE-B',
    'MOVE-TOP-ACTION-PERFORMED', 'MOVE-TOP-SERVER-ACCEPTANCE', 'MOVE-TOP-CONVERGENCE', 'MOVE-TOP-PRESERVATION',
    'UNPIN-NONACTIVE-ACTION-PERFORMED', 'UNPIN-NONACTIVE-SERVER-ACCEPTANCE', 'UNPIN-NONACTIVE-CONVERGENCE', 'UNPIN-NONACTIVE-PRESERVATION',
    'UI54-REMOTE-FOCUS-FIXTURE', 'UI54-FOCUS-REMOTE-REORDER', 'UI54-FOCUS-REMOTE-REMOVE', 'UI54-UIA-ACCESSIBILITY',
    'RUNNER-REMOTE-ACTIONS-PERFORMED', 'MOUSE-PINNED-SELECTION', 'MOUSE-CONTEXT-PIN', 'MOUSE-MOVE-TOP', 'MOUSE-UNPIN',
    'KEYBOARD-NAVIGATION', 'KEYBOARD-CONTEXT', 'KEYBOARD-PIN-ACTION', 'PINNED-ACCESSIBILITY', 'GATE-C-PRESERVATION',
    'REOPEN-BASELINE', 'REOPEN-CLOSE-A', 'REOPEN-PROCESS-EXIT', 'REOPEN-NEW-WINDOW', 'REOPEN-SESSION', 'REOPEN-CAPABILITY', 'REOPEN-CONVERGENCE', 'REOPEN-PERSISTENCE'
)
$gates = [System.Collections.Generic.List[object]]::new()
foreach ($id in $gateIds) {
    if ($id -in @('REOPEN-PROCESS-EXIT', 'REOPEN-NEW-WINDOW')) {
        $record = $machineRecords | Where-Object { $_.Id -eq $id } | Select-Object -Last 1
        if ($record) {
            $gates.Add([pscustomobject]@{ Id = $id; Result = $record.Result; Required = $true; EvidenceProvenance = $record.EvidenceProvenance; Comment = $record.Details; TelemetryCategories = @() })
            continue
        }
    }
    $gates.Add((Get-ResultForGate -Id $id -Records $records -RunnerSteps $runnerSteps))
}
$probeAssociationRecord = $records | Where-Object { $_.RecordType -eq 'checkpoint' -and $_.Id -eq 'INPUT-PROBE-ASSOCIATION' } | Select-Object -Last 1
if ($probeAssociationRecord) {
    $gates.Add((Get-ResultForGate -Id 'INPUT-PROBE-ASSOCIATION' -Records $records -RunnerSteps $runnerSteps))
}

$runnerMachineIds = @('UI54-REMOTE-FOCUS-FIXTURE', 'UI54-FOCUS-REMOTE-REORDER', 'UI54-FOCUS-REMOTE-REMOVE', 'UI54-UIA-ACCESSIBILITY')
$actionGateIds = @('MOVE-TOP-ACTION-PERFORMED', 'UNPIN-NONACTIVE-ACTION-PERFORMED', 'RUNNER-REMOTE-ACTIONS-PERFORMED')
$requiredSetupIds = @('CLIENT-PAIR', 'SERVER-IDENTITY', 'AUTH-SAME-ACCOUNT', 'CAPABILITY', 'INITIAL-CONVERGENCE', 'CONTROLLED-FIXTURE', 'BASELINE-A', 'BASELINE-B')
$setupPassed = $requiredSetupIds.Count -gt 0
foreach ($id in $requiredSetupIds) {
    $setupRecord = $records | Where-Object { $_.RecordType -eq 'checkpoint' -and $_.Id -eq $id } | Select-Object -Last 1
    if ($null -eq $setupRecord -or $setupRecord.Result -ne 'PASS') { $setupPassed = $false; break }
}
$actionConfirmed = @($records | Where-Object { $_.RecordType -eq 'checkpoint' -and $_.Id -in $actionGateIds -and $_.Result -in @('PASS', 'FAIL') }).Count -gt 0
$runtimeStarted = $actionConfirmed -and ([bool]$session.RuntimeAcceptanceStarted -or $setupPassed)
if (-not $runtimeStarted) {
    $runtimeStarted = $setupPassed -and @($runnerSteps | Where-Object { $_.ScenarioId -in @('UI54-FOCUS-REMOTE-REORDER', 'UI54-FOCUS-REMOTE-REMOVE') -and $_.Result -eq 'PASS' }).Count -gt 0
}

$reviewGate = [pscustomobject]@{ Id = 'INDEPENDENT-EVIDENCE-REVIEW'; Result = 'UNVERIFIED'; Required = $true; EvidenceProvenance = 'no evidence'; Comment = 'This completion command does not independently review or certify the operator report.'; TelemetryCategories = @() }
$gates.Add($reviewGate)
$outcome = if ($runtimeStarted) { 'PARTIAL_DESKTOP_ACCEPTANCE' } else { 'OPERATOR_PACKAGE_READY_ACCEPTANCE_PENDING' }
$clientEnd = Get-RepositorySnapshot -Path $repoRoot
$serverRoot = Join-Path (Split-Path -Parent $repoRoot) 'nexIRCServer'
$serverEnd = Get-RepositorySnapshot -Path $serverRoot
$serverIdentityRecord = $records | Where-Object { $_.RecordType -eq 'environment' -and $_.Key -eq 'ServerIdentityLabel' } | Select-Object -Last 1
$serverIdentity = if ($serverIdentityRecord) { $serverIdentityRecord.Value } else { 'not recorded' }
$executionStartedAt = 'not recorded'
try { $executionStartedAt = [DateTimeOffset]::Parse([string]$session.StartedAt).ToString('o') } catch { }
$baseline = [ordered]@{
    Initial = [ordered]@{
        ClientA = Get-BaselineValues -Records $records -Stage 'initial' -Client 'A'
        ClientB = Get-BaselineValues -Records $records -Stage 'initial' -Client 'B'
    }
    AfterMove = [ordered]@{
        ClientA = Get-BaselineValues -Records $records -Stage 'after-move' -Client 'A'
        ClientB = Get-BaselineValues -Records $records -Stage 'after-move' -Client 'B'
    }
    AfterUnpin = [ordered]@{
        ClientA = Get-BaselineValues -Records $records -Stage 'after-unpin' -Client 'A'
        ClientB = Get-BaselineValues -Records $records -Stage 'after-unpin' -Client 'B'
    }
    BeforeReopen = [ordered]@{
        ClientA = Get-BaselineValues -Records $records -Stage 'before-reopen' -Client 'A'
        ClientB = Get-BaselineValues -Records $records -Stage 'before-reopen' -Client 'B'
    }
    AfterReopen = [ordered]@{
        ClientA = Get-BaselineValues -Records $records -Stage 'after-reopen' -Client 'A'
        ClientB = Get-BaselineValues -Records $records -Stage 'after-reopen' -Client 'B'
    }
    FingerprintValuesAreOpaque = $true
    ConversationNamesOrContentRecorded = $false
}
$runnerSummary = @($runnerSteps | Where-Object { $_.ScenarioId -in $runnerMachineIds } | ForEach-Object { [pscustomobject]@{ ScenarioId = $_.ScenarioId; Result = $_.Result; EvidenceProvenance = if ($_.Result -in @('PASS', 'FAIL')) { 'automated observation' } else { 'no evidence' }; EvidenceLevel = $_.EvidenceLevel } })
$telemetryPresent = $false
foreach ($path in @($session.Clients.A.TelemetryPath, $session.Clients.A.ReopenedTelemetryPath)) {
    if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path -PathType Leaf)) { $telemetryPresent = $true }
}
$report = [ordered]@{
    SchemaVersion = '1.0'
    Phase = '54R9'
    Outcome = $outcome
    Phase54Status = 'OPEN'
    Phase55Eligibility = 'NOT AUTHORIZED; requires independent review of authentic two-client acceptance evidence.'
    RunId = $session.RunId
    ExecutionStartedAt = $executionStartedAt
    ReportGeneratedAt = [DateTimeOffset]::Now.ToString('o')
    Environment = [ordered]@{
        ClientRepositoryStart = Get-RepositorySummary -Snapshot $session.ClientRepository
        ClientRepositoryEnd = Get-RepositorySummary -Snapshot $clientEnd
        ServerRepositoryStart = Get-RepositorySummary -Snapshot $session.ServerRepository
        ServerRepositoryEnd = Get-RepositorySummary -Snapshot $serverEnd
        ServerIdentity = $serverIdentity
        ServerLaunchedByPackage = [bool]$session.ServerLaunchedByPackage
        Clients = [ordered]@{
            A = [ordered]@{ Processes = @(Get-ClientIdentitySummary -Client $session.Clients.A); InputProbeEnabled = [bool]$session.Clients.A.InputProbeEnabled; ReopenedInputProbeEnabled = [bool]$session.Clients.A.ReopenedInputProbeEnabled }
            B = [ordered]@{ Processes = @(Get-ClientIdentitySummary -Client $session.Clients.B); InputProbeEnabled = $false }
        }
        AuthenticatedPair = [ordered]@{ Status = if (($records | Where-Object { $_.Id -eq 'AUTH-SAME-ACCOUNT' } | Select-Object -Last 1).Result -eq 'PASS') { 'operator-reported verified' } else { 'not established' }; AccountIdentifierRecorded = $false }
        SynchronizedPinsCapability = [ordered]@{ Status = if (($records | Where-Object { $_.Id -eq 'CAPABILITY' } | Select-Object -Last 1).Result -eq 'PASS') { 'operator-reported verified on both clients' } else { 'not established' }; RequiredCapabilities = @('nexirc/resume', 'nexirc/state', 'nexirc/pins') }
        ServerAcceptanceEvidence = 'The product protocol returns an ACK to the submitting client after server mutation handling. The standard UI acceptance runner does not expose a correlated, independently reviewable server receipt or server-side log. Mark each server-acceptance checkpoint UNVERIFIED unless reliable existing server evidence is separately available.'
    }
    Baseline = $baseline
    Gates = @($gates.ToArray())
    RunnerEvidence = [ordered]@{
        ReportPresent = $runnerReportExists
        Scenarios = $runnerSummary
        DetailsIncluded = $false
        Capability = 'attached read-only process validation, UI Automation rows, projection order, focused pinned-row identity, and post-update keyboard event observations where the runner result supports them'
        Limitations = @('The attached runner does not establish same-account authentication or server acceptance.', 'The runner does not verify composer focus, selection range, draft, transcript, unread, view identity, or PART preservation for the remote action.', 'Windows input telemetry cannot distinguish physical input from UI Automation input.')
    }
    EvidenceProvenance = [ordered]@{
        OperatorReported = 'Guided checkpoint results; free-form comments remain local.'
        AutomatedObservation = 'Launcher process/window identity, attached UI Automation report, and checkpoint-scoped input-probe event classifications.'
        RetainedHistoricalEvidence = @('Phase 54R5 disposable-demo evidence; not an R9 runtime observation.', 'Phase 54R6 closeout and reports; not an R9 runtime observation.')
        Inference = 'None used to pass required gates.'
        NoEvidence = 'Missing, skipped, timed-out, or unverified gates remain UNVERIFIED.'
    }
    Privacy = [ordered]@{
        RawTelemetryPresentLocally = $telemetryPresent
        RawTelemetryIncludedInReport = $false
        RawTelemetryPathIncluded = $false
        ProbeSummary = 'Only event-classification counts from the specific operator checkpoint intervals are included; no hashes, names, key values, coordinates, or content are copied.'
        Comments = 'Free-form checkpoint comments are kept in the local journal and omitted from this report. Operators must not enter account names, message text, draft text, credentials, or tokens.'
        ReportSuitableForSharing = $true
    }
}

$json = ConvertTo-Json -InputObject $report -Depth 16
$gateLines = foreach ($gate in $report.Gates) {
    $comment = (Sanitize-CommentValue -Value ([string]$gate.Comment) -replace '\|', '/') -replace '[\r\n]+', ' '
    $observed = @($gate.TelemetryCategories | ForEach-Object { "$($_.Category)=$($_.Count)" }) -join ', '
    if ([string]::IsNullOrWhiteSpace($observed)) { $observed = 'none recorded' }
    "| ``$($gate.Id)`` | **$($gate.Result)** | $($gate.EvidenceProvenance) | $([bool]$gate.Required) | $observed | $comment |"
}
$decisionText = if ($runtimeStarted) {
    'Authentic operator actions were reported or machine-observed, but independent evidence review is pending. Outcome A is not assigned by this script; Phase 54 remains open.'
} else {
    'No authentic two-client mutation run is recorded. Package preparation is ready for operator execution; Phase 54 remains open.'
}
$mdLines = @(
    '# nexIRC 5 — Phase 54R9 Operator Acceptance Report',
    '',
    "- Outcome: **``$outcome``**",
    '- Phase 54: **OPEN**; Phase 55: **NOT AUTHORIZED**.',
    "- Run: ``$($session.RunId)``; started ``$($report.ExecutionStartedAt)``; report generated ``$($report.ReportGeneratedAt)``.",
    "- Server label: ``$serverIdentity`` (operator-reported, no credentials recorded).",
    "- Client A initial PID: ``$($session.Clients.A.Initial.ProcessId)``; Client B PID: ``$($session.Clients.B.Initial.ProcessId)``; reopened A PID: ``$($session.Clients.A.Reopened.ProcessId)``.",
    "- Client start/end: ``$($session.ClientRepository.Branch)`` ``$($session.ClientRepository.Head)`` / ``$($clientEnd.Branch)`` ``$($clientEnd.Head)``; worktree ``$($clientEnd.Worktree)``; tracking divergence ``$($clientEnd.TrackingDivergence)``.",
    "- Server start/end: ``$($session.ServerRepository.Branch)`` ``$($session.ServerRepository.Head)`` / ``$($serverEnd.Branch)`` ``$($serverEnd.Head)``; worktree ``$($serverEnd.Worktree)``; tracking divergence ``$($serverEnd.TrackingDivergence)``.",
    '',
    '## Gate results',
    '',
    '| Gate | Result | Evidence source | Required | Probe event classes | Comment |',
    '|---|---|---|---:|---|---|'
)
$mdLines += $gateLines
$mdLines += @(
    '',
    '## Baseline fingerprints',
    '',
    'Only operator-entered opaque hex fingerprints are included. Conversation names, message text, draft text, and account identifiers are omitted.',
    '',
    "- Initial A: pin order ``$(Format-BaselineValue $report.Baseline.Initial.ClientA.PinOrder)``; active view ``$(Format-BaselineValue $report.Baseline.Initial.ClientA.ActiveView)``; focus ``$(Format-BaselineValue $report.Baseline.Initial.ClientA.FocusedControl)``; draft ``$(Format-BaselineValue $report.Baseline.Initial.ClientA.Draft)``; transcript ``$(Format-BaselineValue $report.Baseline.Initial.ClientA.Transcript)``; unread ``$(Format-BaselineValue $report.Baseline.Initial.ClientA.Unread)``; view identity ``$(Format-BaselineValue $report.Baseline.Initial.ClientA.ViewIdentity)``.",
    "- Initial B: pin order ``$(Format-BaselineValue $report.Baseline.Initial.ClientB.PinOrder)``.",
    "- After MOVE_TOP: A pin order ``$(Format-BaselineValue $report.Baseline.AfterMove.ClientA.PinOrder)``; B pin order ``$(Format-BaselineValue $report.Baseline.AfterMove.ClientB.PinOrder)``; A active ``$(Format-BaselineValue $report.Baseline.AfterMove.ClientA.ActiveView)``; focus ``$(Format-BaselineValue $report.Baseline.AfterMove.ClientA.FocusedControl)``; draft ``$(Format-BaselineValue $report.Baseline.AfterMove.ClientA.Draft)``; transcript ``$(Format-BaselineValue $report.Baseline.AfterMove.ClientA.Transcript)``; unread ``$(Format-BaselineValue $report.Baseline.AfterMove.ClientA.Unread)``; view identity ``$(Format-BaselineValue $report.Baseline.AfterMove.ClientA.ViewIdentity)``.",
    "- After nonactive UNPIN: A pin order ``$(Format-BaselineValue $report.Baseline.AfterUnpin.ClientA.PinOrder)``; B pin order ``$(Format-BaselineValue $report.Baseline.AfterUnpin.ClientB.PinOrder)``; A active ``$(Format-BaselineValue $report.Baseline.AfterUnpin.ClientA.ActiveView)``; focus ``$(Format-BaselineValue $report.Baseline.AfterUnpin.ClientA.FocusedControl)``; draft ``$(Format-BaselineValue $report.Baseline.AfterUnpin.ClientA.Draft)``; transcript ``$(Format-BaselineValue $report.Baseline.AfterUnpin.ClientA.Transcript)``; unread ``$(Format-BaselineValue $report.Baseline.AfterUnpin.ClientA.Unread)``; view identity ``$(Format-BaselineValue $report.Baseline.AfterUnpin.ClientA.ViewIdentity)``.",
    "- Before reopen: A pin order ``$(Format-BaselineValue $report.Baseline.BeforeReopen.ClientA.PinOrder)``; B pin order ``$(Format-BaselineValue $report.Baseline.BeforeReopen.ClientB.PinOrder)``; A view identity ``$(Format-BaselineValue $report.Baseline.BeforeReopen.ClientA.ViewIdentity)``.",
    "- After reopen: A pin order ``$(Format-BaselineValue $report.Baseline.AfterReopen.ClientA.PinOrder)``; B pin order ``$(Format-BaselineValue $report.Baseline.AfterReopen.ClientB.PinOrder)``; A view identity ``$(Format-BaselineValue $report.Baseline.AfterReopen.ClientA.ViewIdentity)``.",
    '',
    '## Runner and privacy limits',
    '',
    "- Attached UIA report present: ``$runnerReportExists``. Its raw details are retained only in the local run folder and are not copied into this sanitized summary.",
    '- The runner observes Client A UI Automation and event classifications; it does not independently prove server acceptance, authentication pairing, composer/draft/transcript/unread preservation, or the source of physical versus UIA input.',
    '- Raw JSONL telemetry and free-form comments stay local under the ignored Phase 54R9 artifact directory. This report includes no raw telemetry lines, telemetry fingerprints, or free-form comments.',
    '- R5 demo results and R6 closeout are historical context only; they are not counted as R9 runtime evidence.',
    '',
    '## Decision',
    '',
    $decisionText,
    'An independent review must verify every required gate before Phase 54 can close. Phase 55 remains unauthorized.'
)
$markdown = $mdLines -join [Environment]::NewLine

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$sequence = 0
do {
    $suffix = if ($sequence -eq 0) { [Guid]::NewGuid().ToString('N').Substring(0, 8) } else { '{0}-{1:D2}' -f ([Guid]::NewGuid().ToString('N').Substring(0, 8), $sequence) }
    $baseName = "phase54r9-acceptance-report-$stamp-$suffix"
    $jsonPath = Join-Path $runDirectory ($baseName + '.json')
    $markdownPath = Join-Path $runDirectory ($baseName + '.md')
    $sequence++
} while ((Test-Path -LiteralPath $jsonPath) -or (Test-Path -LiteralPath $markdownPath))

$jsonStream = [System.IO.File]::Open($jsonPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
    $jsonStream.Write($bytes, 0, $bytes.Length)
} finally { $jsonStream.Dispose() }
$markdownStream = [System.IO.File]::Open($markdownPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($markdown)
    $markdownStream.Write($bytes, 0, $bytes.Length)
} finally { $markdownStream.Dispose() }

Write-Host "Sanitized acceptance report: $markdownPath" -ForegroundColor Green
Write-Host "Machine-readable report: $jsonPath"
Write-Host "Outcome: $outcome; Phase 54 remains OPEN; Phase 55 remains unauthorized."
