param(
    [Parameter(Mandatory = $true)][string] $AssemblyPath
)

$ErrorActionPreference = 'Stop'
Add-Type -Path ([System.IO.Path]::GetFullPath($AssemblyPath))
$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = [System.IO.Path]::Combine($tempBase, 'ae-native-ingestion-' + [guid]::NewGuid().ToString('N'))
$source = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../contracts/aef/1/interop/examples/otel-aef/run'))
$owner = $null
try {
    $options = [AgentEval.Results.Ingestion.AefIngestionWorkspaceOptions]::new()
    $options.Root = $testRoot
    $options.MaxPendingStages = 1
    $owner = [AgentEval.Results.Ingestion.AefIngestionWorkspace]::new($options)
    $stager = [AgentEval.Results.Ingestion.AefRunStager]::new($owner, $null)
    $importer = [AgentEval.Results.Ingestion.AefLocalRunImporter]::new($stager, $null)
    $finalizer = [AgentEval.Results.Ingestion.AefRunFinalizer]::new($owner, $null, $null)
    $recovery = [AgentEval.Results.Ingestion.AefIngestionRecovery]::new($owner, $finalizer, $null)
    $token = [System.Threading.CancellationToken]::None
    $first = $importer.StageDirectoryAsync($source, $token).GetAwaiter().GetResult()
    $accepted = $finalizer.FinalizeStage($first.Id, $token)
    if (!$accepted.ReceiptArchived) { throw 'Native receipt move failed to archive after lease release.' }
    $retry = $finalizer.FinalizeStage($first.Id, $token)
    if (!$retry.AlreadyAccepted -or !$retry.ReceiptArchived) { throw 'Archived receipt retry failed.' }
    $duplicate = $importer.StageDirectoryAsync($source, $token).GetAwaiter().GetResult()
    $deduplicated = $finalizer.FinalizeStage($duplicate.Id, $token)
    if (!$deduplicated.AlreadyAccepted -or !$deduplicated.ReceiptArchived) { throw 'Duplicate archival failed.' }
    if (Test-Path -LiteralPath $duplicate.Directory) { throw 'Duplicate scratch content remains.' }
    $failed = $owner.BeginStage($token)
    [System.IO.Directory]::CreateDirectory((Join-Path $failed.Directory 'content')) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $failed.Directory 'content/partial'), 'scratch')
    $state = @{ schemaVersion = '1.0'; id = $failed.Id; status = 'failed'; files = 0; bytes = 0 } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText((Join-Path $failed.Directory 'state.json'), $state)
    $discarded = $recovery.DiscardStage($failed.Id, $token)
    if ($discarded.Status -ne 'discarded' -or !$discarded.ReceiptArchived) { throw 'Native discard archival failed.' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $testRoot '.staging') -Force).Count -ne 0) { throw 'Pending capacity was not released.' }
    if (@(Get-ChildItem -LiteralPath $owner.RunsDirectory -Directory).Count -ne 1) { throw 'Accepted evidence changed during cleanup.' }
    Write-Output 'PASS: native workspace publication, Windows-compatible receipt move, archived retry, duplicate cleanup and explicit discard.'
}
finally {
    if ($null -ne $owner) { $owner.Dispose() }
    # Check the resolved absolute target before recursively removing this fixture-owned temporary directory.
    $resolved = [System.IO.Path]::GetFullPath($testRoot)
    $prefix = [System.IO.Path]::TrimEndingDirectorySeparator($tempBase) + [System.IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        ![System.IO.Path]::GetFileName($resolved).StartsWith('ae-native-ingestion-', [System.StringComparison]::Ordinal)) {
        throw 'Refusing cleanup outside the native fixture temporary root.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
