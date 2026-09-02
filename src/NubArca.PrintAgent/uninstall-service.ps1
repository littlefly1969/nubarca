[CmdletBinding()]
param(
    [string] $ServiceName = 'NubArcaPrintAgent',
    [switch] $PurgeLocalState
)

$ErrorActionPreference = 'Stop'
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unable to remove service $ServiceName" }
}
if ($PurgeLocalState) {
    $dataDirectory = Join-Path $env:ProgramData 'NubArca\PrintAgent'
    if (Test-Path -LiteralPath $dataDirectory) {
        Remove-Item -LiteralPath $dataDirectory -Recurse -Force
        Write-Host "Removed local credential, journal and temporary data from $dataDirectory."
    }
} else {
    Write-Host 'Service removed. Local credential and journal were preserved.'
}
