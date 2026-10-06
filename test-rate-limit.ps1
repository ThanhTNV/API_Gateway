<#
.SYNOPSIS
    Fires concurrent bursts at the gateway and reports how many requests were rate limited (429).

.EXAMPLE
    .\test-rate-limit.ps1
    .\test-rate-limit.ps1 -BaseUrl http://localhost:8080/api/todos -Id 3 -Requests 500
#>
param(
    [string]$BaseUrl = 'http://localhost:8080/todos',
    [string]$Id = '1',
    # Must be well above the policy's burst size (PermitLimit, 100 by default) to trigger 429s.
    [int]$Requests = 300
)

Add-Type -AssemblyName System.Net.Http
[System.Net.ServicePointManager]::DefaultConnectionLimit = 1000

$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds(30)

function Invoke-Burst([string]$Url, [int]$Count) {
    Write-Host ""
    Write-Host "== $Count concurrent requests -> $Url" -ForegroundColor Cyan

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $tasks = 1..$Count | ForEach-Object { $client.GetAsync($Url) }
    try { [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$tasks) } catch { }
    $watch.Stop()

    $codes = @{}
    $retryAfter = $null
    foreach ($task in $tasks) {
        if ($task.Status -ne 'RanToCompletion') {
            $key = 'failed'
        }
        else {
            $response = $task.Result
            $key = [string][int]$response.StatusCode
            if ($key -eq '429' -and $null -eq $retryAfter -and $response.Headers.RetryAfter) {
                $retryAfter = $response.Headers.RetryAfter.Delta.TotalSeconds
            }
            $response.Dispose()
        }
        $codes[$key] = 1 + [int]$codes[$key]
    }

    foreach ($key in ($codes.Keys | Sort-Object)) {
        $color = if ($key -eq '429') { 'Yellow' } elseif ($key -like '2*') { 'Green' } else { 'Red' }
        Write-Host ("   {0,-6} x {1}" -f $key, $codes[$key]) -ForegroundColor $color
    }
    Write-Host ("   took {0} ms, Retry-After: {1}" -f $watch.ElapsedMilliseconds, $(if ($null -ne $retryAfter) { "$retryAfter s" } else { '(none)' }))

    [pscustomobject]@{ Limited = [int]$codes['429']; Failed = [int]$codes['failed']; NotFound = [int]$codes['404']; RetryAfter = $retryAfter }
}

$listUrl = $BaseUrl.TrimEnd('/')
$itemUrl = "$listUrl/$Id"

$first = Invoke-Burst $listUrl $Requests

if ($first.Failed -eq $Requests) {
    Write-Host "`nNo request reached the gateway. Is it running at $listUrl ?" -ForegroundColor Red
    exit 1
}
if ($first.NotFound -eq $Requests) {
    # Unmatched paths never reach a route, so they are not rate limited either.
    Write-Host "`nEvery request got 404: $listUrl does not match a gateway route. Check the route's PathPrefix." -ForegroundColor Red
    exit 1
}

# Wait for the bucket to refill so the second burst starts from a full allowance.
$wait = [Math]::Max(3, [int]$first.RetryAfter + 2)
Write-Host "`nWaiting $wait s for the limiter to refill..."
Start-Sleep -Seconds $wait

$second = Invoke-Burst $itemUrl $Requests

$wait = [Math]::Max(3, [int]$second.RetryAfter + 2)
Write-Host "`nWaiting $wait s, then checking that requests are accepted again..."
Start-Sleep -Seconds $wait
$recovered = Invoke-Burst $itemUrl 1

Write-Host ""
if ($first.Limited -gt 0 -and $second.Limited -gt 0 -and $recovered.Limited -eq 0) {
    Write-Host "PASS: both URLs were rate limited under burst and recovered afterwards." -ForegroundColor Green
}
elseif ($first.Limited -eq 0 -and $second.Limited -eq 0) {
    Write-Host "FAIL: no 429 seen. Raise -Requests, or check that the route has a RateLimitPolicy." -ForegroundColor Red
    exit 1
}
else {
    Write-Host "CHECK: results were mixed, see the counts above." -ForegroundColor Yellow
    exit 1
}
