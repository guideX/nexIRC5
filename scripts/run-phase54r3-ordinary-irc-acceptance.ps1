[CmdletBinding()]
param(
    [ValidateRange(30, 240)]
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\nexIRC.Desktop\nexIRC.Desktop.csproj"
if (-not (Test-Path -LiteralPath $projectPath)) { throw "Desktop project not found: $projectPath" }

$suffix = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$channel = "#nexirc54r3$suffix"
$nickname = "NexR54$suffix"
$startInfo = [System.Diagnostics.ProcessStartInfo]::new("dotnet")
$startInfo.WorkingDirectory = $repoRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $false
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
foreach ($argument in @(
    "run", "--project", $projectPath, "--no-build", "--configuration", "Debug", "--",
    "--phase54r3-ordinary-irc", "--host", "testnet.inspircd.org", "--port", "6697", "--nickname", $nickname
)) { $startInfo.ArgumentList.Add($argument) }

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
$process.EnableRaisingEvents = $true
$lines = [System.Collections.Generic.List[string]]::new()
$script:pendingLineRead = $null
$stderrTask = $null
$scenarioPassed = $false

function Read-HarnessLine {
    param([int]$WaitMilliseconds = 500)
    if ($null -eq $script:pendingLineRead) {
        $script:pendingLineRead = $process.StandardOutput.ReadLineAsync()
    }
    if (-not $script:pendingLineRead.Wait($WaitMilliseconds)) { return $null }
    $line = $script:pendingLineRead.Result
    $script:pendingLineRead = $null
    if ($null -ne $line) { $lines.Add($line) }
    return $line
}

function Wait-HarnessPrefix {
    param([string]$Prefix, [int]$Seconds = 15)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = Read-HarnessLine -WaitMilliseconds 400
        if ($null -eq $line) {
            if ($process.HasExited) { throw "Harness exited with code $($process.ExitCode) before: $Prefix" }
            continue
        }
        if ($line.StartsWith($Prefix, [StringComparison]::Ordinal)) { return $line }
    }
    throw "Timed out waiting for harness output: $Prefix"
}

function Invoke-HarnessCommand {
    param([string]$Command, [string]$ExpectedPrefix, [int]$Seconds = 15)
    $process.StandardInput.WriteLine($Command)
    $process.StandardInput.Flush()
    return Wait-HarnessPrefix -Prefix $ExpectedPrefix -Seconds $Seconds
}

try {
    if (-not $process.Start()) { throw "Could not start the test-owned nexIRC process." }
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $ready = Wait-HarnessPrefix -Prefix "PHASE54R3_STATUS kind=ready" -Seconds $TimeoutSeconds
    if (($ready -notmatch "state=(Connected|Registered) registration=Registered tls=true") -or
        ($ready -notmatch "pinsAdvertised=false pinsEnabled=false") -or
        ($ready -notmatch "ircd=InspIRCd")) {
        throw "Live ordinary-server preflight did not match expectations: $ready"
    }

    $joined = Invoke-HarnessCommand "JOIN $channel" "PHASE54R3_JOIN"
    if ($joined -notmatch "outcome=joined") { throw "Disposable channel join failed: $joined" }
    $pin = Invoke-HarnessCommand "PIN $channel" "PHASE54R3_PIN operation=PIN"
    if ($pin -notmatch "accepted=true projected=true") { throw "Local channel PIN failed: $pin" }
    $statusAfterPin = Invoke-HarnessCommand "STATUS" "PHASE54R3_STATUS kind=status"
    if ($statusAfterPin -notmatch "registration=Registered.*pinsAdvertised=false pinsEnabled=false.*channels=1 localPins=1") {
        throw "Local channel PIN did not remain in the live ordinary-server projection: $statusAfterPin"
    }

    $unpin = Invoke-HarnessCommand "UNPIN $channel" "PHASE54R3_PIN operation=UNPIN"
    if ($unpin -notmatch "accepted=true projected=false") { throw "Local channel UNPIN failed: $unpin" }
    $statusAfterUnpin = Invoke-HarnessCommand "STATUS" "PHASE54R3_STATUS kind=status"
    if ($statusAfterUnpin -notmatch "registration=Registered.*channels=1 localPins=0") {
        throw "Channel or ordinary connection did not remain usable after UNPIN: $statusAfterUnpin"
    }

    $queryNickname = "Peer$suffix"
    $queryPin = Invoke-HarnessCommand "QUERY_PIN $queryNickname" "PHASE54R3_QUERY_PIN operation=PIN"
    if ($queryPin -notmatch "accepted=true projected=true accountKey=absent") {
        throw "Nick-only query PIN was not kept local: $queryPin"
    }
    $queryUnpin = Invoke-HarnessCommand "QUERY_UNPIN $queryNickname" "PHASE54R3_QUERY_PIN operation=UNPIN"
    if ($queryUnpin -notmatch "accepted=true projected=false accountKey=absent") {
        throw "Nick-only query UNPIN failed: $queryUnpin"
    }

    $trace = Invoke-HarnessCommand "TRACE" "PHASE54R3_TRACE"
    if ($trace -notmatch "pinNegotiationRequests=0 nexircCommands=0") {
        throw "Pin protocol traffic escaped onto the ordinary server connection: $trace"
    }
    $statusFinal = Invoke-HarnessCommand "STATUS" "PHASE54R3_STATUS kind=status"
    if ($statusFinal -notmatch "registration=Registered.*channels=1 localPins=0") {
        throw "The ordinary IRC session was not healthy after local pin actions: $statusFinal"
    }
    $scenarioPassed = $true
    Write-Output "ORDINARY_IRC_ACCEPTANCE=PASS"
}
catch {
    Write-Output "ORDINARY_IRC_ACCEPTANCE=INCOMPLETE"
    Write-Output ("DRIVER_ERROR=" + $_.Exception.GetType().Name + ": " + $_.Exception.Message)
}
finally {
    if (-not $process.HasExited) {
        try {
            $process.StandardInput.WriteLine("EXIT")
            $process.StandardInput.Flush()
        } catch { }
        if (-not $process.WaitForExit(10000)) {
            $ownedPid = $process.Id
            $process.Kill($true)
            $process.WaitForExit(5000)
            Write-Output "TEST_OWNED_PROCESS_REAPED=$ownedPid"
        }
    }
    if ($process.HasExited) {
        Write-Output "TEST_OWNED_PROCESS_EXIT=$($process.ExitCode)"
    }
    if ($stderrTask -and $stderrTask.IsCompleted -and -not [string]::IsNullOrWhiteSpace($stderrTask.Result)) {
        Write-Output "HARNESS_STDERR=$($stderrTask.Result.Trim() -replace '[\r\n]+', ' ')"
    }
    foreach ($line in $lines) { Write-Output $line }
    $process.Dispose()
}

if (-not $scenarioPassed) { exit 1 }
