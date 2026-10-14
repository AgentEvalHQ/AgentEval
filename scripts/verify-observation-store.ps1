param(
    [Parameter(Mandatory = $true)][string] $AssemblyPath,
    [ValidateSet('parent', 'write', 'retry')][string] $Phase = 'parent',
    [string] $FixtureRoot
)

$ErrorActionPreference = 'Stop'
$assembly = [System.IO.Path]::GetFullPath($AssemblyPath)
if ($Phase -ne 'parent') {
    Add-Type -Path $assembly
    $token = [System.Threading.CancellationToken]::None
    $options = [AgentEval.Results.Observations.OtlpObservationStoreOptions]::new()
    $options.Root = $FixtureRoot
    $options.Source = 'native-process-fixture'
    $options.MaxRecords = 1
    $normalizer = [AgentEval.Results.Observations.OtlpTraceNormalizer]::new($null)
    $grouper = [AgentEval.Results.Observations.OtlpSessionGrouper]::new($normalizer)
    $policy = [AgentEval.Results.Observations.OtlpContentPolicy]::new()
    $store = [AgentEval.Results.Observations.OtlpObservationStore]::new($options, $policy, $grouper, $token)
    try {
        $json = '{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"service"}}]},"scopeSpans":[{"spans":[{"traceId":"0123456789abcdef0123456789abcdef","spanId":"0123456789abcdef","name":"private-prompt","attributes":[{"key":"gen_ai.request.model","value":{"stringValue":"model"}},{"key":"session.id","value":{"stringValue":"session"}},{"key":"gen_ai.provider.name","value":{"stringValue":"provider"}},{"key":"gen_ai.usage.input_tokens","value":{"intValue":"9007199254740993"}}]}]}]}]}' # DevSkim: ignore DS173237 - synthetic OTLP trace identity and fixture payload
        $decoder = [AgentEval.Results.Observations.OtlpJsonTraceDecoder]::new($null)
        $payload = [System.ReadOnlyMemory[byte]]::new([System.Text.Encoding]::UTF8.GetBytes($json))
        $receipt = $store.Accept($decoder.Decode($payload, $token), $token)
        if ($receipt.AlreadyAccepted -ne ($Phase -eq 'retry')) { throw 'Incorrect cross-process retry result.' }
        if ($store.List($token).Length -ne 1) { throw 'Retry duplicated the accepted record.' }
        $record = $store.Read($receipt.Id, $token)
        if ($record.Content.Batch.Resources[0].Scopes[0].Spans[0].Name -ne '') { throw 'Default-off policy was bypassed.' }
        $field = $record.Sessions[0].Members[0].Span.Field('tokens.input')
        if ($field.Value.Value -ne 9007199254740993L) { throw 'Integer precision changed during storage.' }
        Write-Output $receipt.Id
    }
    finally { $store.Dispose() }
    exit 0
}

$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = Join-Path $tempBase ('ae-native-observations-' + [guid]::NewGuid().ToString('N'))
$pwsh = (Get-Process -Id $PID).Path
try {
    $first = & $pwsh -NoProfile -File $PSCommandPath -AssemblyPath $assembly -Phase write -FixtureRoot $testRoot
    if ($LASTEXITCODE -ne 0) { throw 'Native writer process failed.' }
    [System.IO.File]::WriteAllText((Join-Path $testRoot 'incoming.tmp'), 'interrupted')
    $retry = & $pwsh -NoProfile -File $PSCommandPath -AssemblyPath $assembly -Phase retry -FixtureRoot $testRoot
    if ($LASTEXITCODE -ne 0) { throw 'Native retry process failed.' }
    if ($first -ne $retry) { throw 'Record ID changed across process restart.' }
    if (Test-Path -LiteralPath (Join-Path $testRoot 'incoming.tmp')) { throw 'Interrupted scratch was not recovered.' }
    Write-Output 'PASS: native content filtering, atomic publication, exact retry across processes, integer precision and scratch recovery.'
}
finally {
    $resolved = [System.IO.Path]::GetFullPath($testRoot)
    $prefix = [System.IO.Path]::TrimEndingDirectorySeparator($tempBase) + [System.IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        ![System.IO.Path]::GetFileName($resolved).StartsWith('ae-native-observations-', [System.StringComparison]::Ordinal)) {
        throw 'Refusing cleanup outside the native observation fixture root.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
