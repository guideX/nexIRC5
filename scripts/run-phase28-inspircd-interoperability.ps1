[CmdletBinding()]
param(
    [ValidateSet("inspircd-local-full", "inspircd-local-noecho", "inspircd-local-nomsgid", "inspircd-local-restricted-tags", "inspircd-local-no-tags", "inspircd-local-no-message-tags", "inspircd-local-no-server-time")]
    [string]$Profile = "inspircd-local-full",
    [string]$InspIRCdRoot,
    [string]$ExecutablePath,
    [ValidateRange(0, 65535)]
    [int]$Port = 0,
    [ValidateRange(30, 600)]
    [int]$TimeoutSeconds = 120,
    [switch]$RunVarianceProfiles,
    [string]$OutputPath,
    [string]$ComparisonPath
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\nexIRC.Interoperability\nexIRC.Interoperability.csproj"
$templatePath = Join-Path $repoRoot "scripts\phase28\inspircd\inspircd.conf.template"
$artifactRoot = Join-Path $repoRoot "artifacts\phase28"
$serverRoot = Join-Path $artifactRoot "server"
$runRoot = Join-Path $serverRoot "runs"
$null = New-Item -ItemType Directory -Path $runRoot -Force

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Phase 28 interoperability project was not found: $projectPath"
}
if (-not (Test-Path -LiteralPath $templatePath)) {
    throw "Phase 28 InspIRCd configuration template was not found: $templatePath"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The dotnet SDK is required to run the Phase 28 interoperability harness."
}

function Resolve-InspIRCdExecutable {
    param([string]$RequestedPath, [string]$RequestedRoot)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates.Add($RequestedPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($RequestedRoot)) {
        $candidates.Add((Join-Path $RequestedRoot "bin\inspircd.exe"))
        $candidates.Add((Join-Path $RequestedRoot "inspircd.exe"))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:INSPIRCD_HOME)) {
        $candidates.Add((Join-Path $env:INSPIRCD_HOME "bin\inspircd.exe"))
        $candidates.Add((Join-Path $env:INSPIRCD_HOME "inspircd.exe"))
    }
    $candidates.Add((Join-Path $repoRoot "artifacts\phase28\server\runtime\bin\inspircd.exe"))
    $candidates.Add("C:\Program Files\InspIRCd\bin\inspircd.exe")
    $candidates.Add("C:\Program Files\InspIRCd\inspircd.exe")

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }

    $searched = ($candidates | Select-Object -Unique) -join "; "
    throw "InspIRCd v4 runtime unavailable. No executable was found in the explicit path/root, INSPIRCD_HOME, the ignored Phase 28 runtime area, or the standard Windows package locations. Searched: $searched. The official Windows package is an administrator-required installer; install it separately or provide -ExecutablePath/-InspIRCdRoot."
}

function Select-LocalPort {
    param([int]$RequestedPort)

    if ($RequestedPort -gt 0) {
        return $RequestedPort
    }

    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Get-ProfileModules {
    param([string]$SelectedProfile)

    $modules = [System.Collections.Generic.List[string]]::new()
    @(
        "ircv3",
        "ircv3_accounttag",
        "ircv3_batch",
        "ircv3_capnotify",
        "ircv3_ctctags",
        "ircv3_echomessage",
        "ircv3_labeledresponse",
        "ircv3_msgid",
        "ircv3_servertime",
        "chanhistory"
    ) | ForEach-Object { $modules.Add($_) }

    switch ($SelectedProfile) {
        "inspircd-local-noecho" { $modules.Remove("ircv3_echomessage") }
        "inspircd-local-nomsgid" { $modules.Remove("ircv3_msgid") }
        "inspircd-local-no-message-tags" { $modules.Remove("ircv3_ctctags") }
        "inspircd-local-no-server-time" { $modules.Remove("ircv3_servertime") }
    }

    return $modules.ToArray()
}

function Get-ClientTagPolicy {
    param([string]$SelectedProfile)

    if ($SelectedProfile -eq "inspircd-local-restricted-tags") {
        return "known"
    }
    if ($SelectedProfile -eq "inspircd-local-no-tags") { return "none" }
    if ($SelectedProfile -eq "inspircd-local-no-message-tags") { return $null }
    return "all"
}

function Convert-ToConfigPath {
    param([string]$Path)
    return $Path.Replace("\", "/")
}

function Wait-ForLocalPort {
    param([int]$ListenPort, [int]$ProcessId, [int]$DeadlineSeconds = 20)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($DeadlineSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            return $false
        }
        $client = [System.Net.Sockets.TcpClient]::new()
        try {
            $connect = $client.ConnectAsync("127.0.0.1", $ListenPort)
            if ($connect.Wait(250) -and $client.Connected) {
                return $true
            }
        }
        catch {
        }
        finally {
            $client.Dispose()
        }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Get-InspIRCdVersion {
    param([string]$Path)

    try {
        $versionOutput = (& $Path --version 2>$null | Out-String).Trim()
        $match = [regex]::Match($versionOutput, "\b4\.\d+\.\d+\b")
        if ($match.Success) {
            return $match.Value
        }
        if (-not [string]::IsNullOrWhiteSpace($versionOutput)) {
            return $versionOutput
        }
    }
    catch {
    }
    return "unknown"
}

function Invoke-Profile {
    param(
        [string]$SelectedProfile,
        [string]$InspIRCdPath,
        [int]$RequestedPort,
        [int]$RunTimeoutSeconds
    )

    $stamp = Get-Date -Format "yyyyMMdd-HHmmssfff"
    $profileRunRoot = Join-Path $runRoot ("{0}-{1}" -f $SelectedProfile, $stamp)
    $configRoot = Join-Path $profileRunRoot "conf"
    $dataRoot = Join-Path $profileRunRoot "data"
    $logRoot = Join-Path $profileRunRoot "logs"
    $runtimeRoot = Join-Path $profileRunRoot "runtime"
    $null = New-Item -ItemType Directory -Path $configRoot,$dataRoot,$logRoot,$runtimeRoot -Force
    $portForRun = Select-LocalPort $RequestedPort
    $modules = @(Get-ProfileModules $SelectedProfile)
    $policy = Get-ClientTagPolicy $SelectedProfile
    $moduleConfig = ($modules | ForEach-Object { '<module name="{0}">' -f $_ }) -join [Environment]::NewLine
    $ctctagsConfig = if ($modules -contains "ircv3_ctctags") { '<ctctags clientonlytags="{0}">' -f $policy } else { "" }
    $template = Get-Content -Raw -LiteralPath $templatePath
    $config = $template
    $config = $config.Replace("@PORT@", $portForRun.ToString([Globalization.CultureInfo]::InvariantCulture))
    $config = $config.Replace("@CONFIGDIR@", (Convert-ToConfigPath $configRoot))
    $config = $config.Replace("@DATADIR@", (Convert-ToConfigPath $dataRoot))
    $config = $config.Replace("@LOGDIR@", (Convert-ToConfigPath $logRoot))
    $moduleRoot = Split-Path -Parent (Split-Path -Parent $InspIRCdPath)
    $config = $config.Replace("@MODULEDIR@", (Convert-ToConfigPath (Join-Path $moduleRoot "modules")))
    $config = $config.Replace("@RUNTIMEDIR@", (Convert-ToConfigPath $runtimeRoot))
    $config = $config.Replace("@CLIENTTAGPOLICY@", ($policy ?? "none"))
    $config = $config.Replace("@MODULES@", $moduleConfig)
    $config = $config.Replace("@CTCTAGS_CONFIG@", $ctctagsConfig)
    $configPath = Join-Path $configRoot "inspircd.conf"
    $motdPath = Join-Path $configRoot "motd.example.txt"
    [System.IO.File]::WriteAllText($configPath, $config, [Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($motdPath, ("nexIRC Phase 28 disposable InspIRCd server" + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

    $stdoutPath = Join-Path $logRoot "server.stdout.log"
    $stderrPath = Join-Path $logRoot "server.stderr.log"
    $serverProcess = $null
    $harnessExitCode = 1
    $resultPath = Join-Path $artifactRoot ("{0}-result.json" -f $SelectedProfile)
    $transcriptPath = Join-Path $artifactRoot ("{0}-transcript.jsonl" -f $SelectedProfile)
    try {
        $workingRoot = Split-Path -Parent (Split-Path -Parent $InspIRCdPath)
        $serverProcess = Start-Process -FilePath $InspIRCdPath -ArgumentList @("--config", $configPath, "--nofork", "--nopid") -WorkingDirectory $workingRoot -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -WindowStyle Hidden -PassThru
        if (-not (Wait-ForLocalPort $portForRun $serverProcess.Id)) {
            $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -Raw -LiteralPath $stderrPath } else { "" }
            throw "InspIRCd process $($serverProcess.Id) did not open 127.0.0.1:$portForRun within the bounded startup window. $stderr"
        }

        $harnessArguments = @(
            "run", "--project", $projectPath, "--no-restore", "--",
            "--profile", $SelectedProfile,
            "--server", "127.0.0.1",
            "--port", $portForRun.ToString([Globalization.CultureInfo]::InvariantCulture),
            "--no-tls",
            "--timeout-seconds", $RunTimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
            "--output", $resultPath,
            "--transcript", $transcriptPath
        )
        & dotnet @harnessArguments
        $harnessExitCode = $LASTEXITCODE
        if ($harnessExitCode -ne 0 -and -not (Test-Path -LiteralPath $resultPath)) {
            throw "The interoperability harness exited with code $harnessExitCode without writing $resultPath."
        }
    }
    finally {
        if ($null -ne $serverProcess) {
            $serverProcess.Refresh()
            if (-not $serverProcess.HasExited) {
                [void]$serverProcess.CloseMainWindow()
                if (-not $serverProcess.WaitForExit(5000)) {
                    Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
                    $serverProcess.WaitForExit(5000)
                }
            }
        }
    }

    [pscustomobject]@{
        Profile = $SelectedProfile
        Implementation = "InspIRCd"
        Version = Get-InspIRCdVersion $InspIRCdPath
        Endpoint = "127.0.0.1:$portForRun"
        Tls = $false
        ConfiguredModules = $modules
        ConfiguredClientTagPolicy = $policy ?? "not-applicable"
        EffectiveConfig = $configPath
        Result = $resultPath
        Transcript = $transcriptPath
        HarnessExitCode = $harnessExitCode
        Cleanup = [bool]($null -eq $serverProcess -or $serverProcess.HasExited)
    }
}

$selectedProfiles = if ($RunVarianceProfiles) {
    @(
        "inspircd-local-full",
        "inspircd-local-noecho",
        "inspircd-local-nomsgid",
        "inspircd-local-restricted-tags",
        "inspircd-local-no-tags",
        "inspircd-local-no-message-tags",
        "inspircd-local-no-server-time"
    )
} else {
    @($Profile)
}

$resolvedExecutable = $null
$profileResults = [System.Collections.Generic.List[object]]::new()
$overallExitCode = 0
try {
    $resolvedExecutable = Resolve-InspIRCdExecutable $ExecutablePath $InspIRCdRoot
    foreach ($selectedProfile in $selectedProfiles) {
        try {
            $profileResult = Invoke-Profile $selectedProfile $resolvedExecutable $Port $TimeoutSeconds
            $profileResults.Add($profileResult)
            if ($profileResult.HarnessExitCode -ne 0 -or -not $profileResult.Cleanup) {
                $overallExitCode = 1
            }
        }
        catch {
            $overallExitCode = 1
            $profileResults.Add([pscustomobject]@{
                Profile = $selectedProfile
                Implementation = "InspIRCd"
                Version = if ($resolvedExecutable) { Get-InspIRCdVersion $resolvedExecutable } else { "unknown" }
                Endpoint = $null
                Tls = $false
                ConfiguredModules = @(Get-ProfileModules $selectedProfile)
                ConfiguredClientTagPolicy = (Get-ClientTagPolicy $selectedProfile) ?? "not-applicable"
                EffectiveConfig = $null
                Result = $null
                Transcript = $null
                HarnessExitCode = 1
                Cleanup = $true
                Failure = $_.Exception.Message
            })
            if (-not $RunVarianceProfiles) {
                throw
            }
        }
    }
}
catch {
    $overallExitCode = 1
    $failure = $_.Exception.Message
    $blocked = [ordered]@{
        SchemaVersion = "nexIRC-phase28-v1"
        Outcome = "SECOND_SERVER_ENVIRONMENT_BLOCKED"
        Success = $false
        Implementation = "InspIRCd"
        RequestedProfiles = $selectedProfiles
        Failure = $failure
        Cleanup = $true
    }
    $blockedPath = if ([string]::IsNullOrWhiteSpace($OutputPath)) { Join-Path $artifactRoot "inspircd-result.json" } elseif ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $blockedPath) -Force
    $blocked | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $blockedPath -Encoding utf8
    $blockedComparisonPath = if ([string]::IsNullOrWhiteSpace($ComparisonPath)) { Join-Path $artifactRoot "server-comparison.json" } elseif ([System.IO.Path]::IsPathRooted($ComparisonPath)) { $ComparisonPath } else { Join-Path $repoRoot $ComparisonPath }
    $blockedComparison = [ordered]@{
        SchemaVersion = "nexIRC-phase28-comparison-v1"
        Outcome = "SECOND_SERVER_ENVIRONMENT_BLOCKED"
        Ergo = $null
        InspIRCd = [ordered]@{
            Implementation = "InspIRCd"
            RequestedProfiles = $selectedProfiles
            Result = $blockedPath
            Failure = $failure
        }
    }
    $baselineErgoPath = Join-Path $repoRoot "artifacts\phase27\live-result.json"
    if (Test-Path -LiteralPath $baselineErgoPath) {
        try {
            $baselineErgo = Get-Content -Raw -LiteralPath $baselineErgoPath | ConvertFrom-Json
            $blockedComparison.Ergo = [ordered]@{
                Implementation = "Ergo"
                Endpoint = $baselineErgo.Endpoint
                Server = $baselineErgo.Server
                Scenarios = $baselineErgo.Scenarios
                Result = $baselineErgoPath
            }
        }
        catch {
            $blockedComparison.Ergo = [ordered]@{ Result = $baselineErgoPath; ParseError = $_.Exception.Message }
        }
    }
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $blockedComparisonPath) -Force
    $blockedComparison | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $blockedComparisonPath -Encoding utf8
    Write-Error $failure
    exit $overallExitCode
}

$fullResult = $null
foreach ($profileResult in $profileResults) {
    if ($null -ne $profileResult.Result -and (Test-Path -LiteralPath $profileResult.Result)) {
        try {
            $profileResult | Add-Member -NotePropertyName ResultDocument -NotePropertyValue (Get-Content -Raw -LiteralPath $profileResult.Result | ConvertFrom-Json) -Force
        }
        catch {
            $profileResult | Add-Member -NotePropertyName ResultDocument -NotePropertyValue $null -Force
        }
    }
    if ($profileResult.Profile -eq "inspircd-local-full" -and $null -ne $profileResult.ResultDocument) {
        $fullResult = $profileResult.ResultDocument
    }
}

$coreScenarioNames = @("canonical-parent", "reply-relay", "reaction-lifecycle", "unreaction")
$fullCorePassed = $false
if ($null -ne $fullResult) {
    $fullClassifications = @($fullResult.Scenarios.Classifications)
    $passedNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($classification in $fullClassifications) {
        if ($classification.Status -eq "Passed" -and $coreScenarioNames -contains $classification.Name) {
            $null = $passedNames.Add($classification.Name)
        }
    }
    $fullCorePassed = $passedNames.Count -eq $coreScenarioNames.Count
}
$varianceFailures = @($profileResults | Where-Object { $_.HarnessExitCode -ne 0 -or -not $_.Cleanup -or $null -ne $_.Failure })
$finalOutcome = if ($fullCorePassed -and $varianceFailures.Count -eq 0 -and $RunVarianceProfiles) {
    "MULTI_SERVER_INTEROPERABILITY_VERIFIED"
}
elseif ($fullCorePassed) {
    "SECOND_SERVER_CORE_INTEROP_VERIFIED"
}
else {
    "SECOND_SERVER_CAPABILITY_VARIANCE_SAFE"
}

$finalDocument = [ordered]@{
    SchemaVersion = "nexIRC-phase28-v1"
    Outcome = $finalOutcome
    Success = ($overallExitCode -eq 0 -and $fullCorePassed)
    Implementation = "InspIRCd"
    RequestedProfiles = $selectedProfiles
    Executable = $resolvedExecutable
    Profiles = $profileResults
    Limitations = @(
        "InspIRCd behavior is only claimed for profiles whose harness result was produced.",
        "A missing optional capability is classified as unsupported or blocked-by-server rather than as a nexIRC protocol failure."
    )
    Cleanup = (@($profileResults | Where-Object { -not $_.Cleanup }).Count -eq 0)
}
$finalPath = if ([string]::IsNullOrWhiteSpace($OutputPath)) { Join-Path $artifactRoot "inspircd-result.json" } elseif ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $finalPath) -Force
$finalDocument | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $finalPath -Encoding utf8

$comparisonDocument = [ordered]@{
    SchemaVersion = "nexIRC-phase28-comparison-v1"
    Outcome = $finalOutcome
    Ergo = $null
    InspIRCd = [ordered]@{
        Implementation = "InspIRCd"
        Profiles = $profileResults
        Result = $finalPath
    }
}
$ergoPath = Join-Path $repoRoot "artifacts\phase27\live-result.json"
if (Test-Path -LiteralPath $ergoPath) {
    try {
        $ergo = Get-Content -Raw -LiteralPath $ergoPath | ConvertFrom-Json
        $comparisonDocument.Ergo = [ordered]@{
            Implementation = "Ergo"
            Endpoint = $ergo.Endpoint
            Server = $ergo.Server
            Scenarios = $ergo.Scenarios
            Result = $ergoPath
        }
    }
    catch {
        $comparisonDocument.Ergo = [ordered]@{ Result = $ergoPath; ParseError = $_.Exception.Message }
    }
}
$comparisonPathResolved = if ([string]::IsNullOrWhiteSpace($ComparisonPath)) { Join-Path $artifactRoot "server-comparison.json" } elseif ([System.IO.Path]::IsPathRooted($ComparisonPath)) { $ComparisonPath } else { Join-Path $repoRoot $ComparisonPath }
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $comparisonPathResolved) -Force
$comparisonDocument | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $comparisonPathResolved -Encoding utf8

Write-Host ("Phase 28 InspIRCd outcome: {0}" -f $finalOutcome)
Write-Host ("Result: {0}" -f $finalPath)
Write-Host ("Comparison: {0}" -f $comparisonPathResolved)
exit $overallExitCode
