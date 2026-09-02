[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidatePattern('^https://')] [string] $ServerOrigin,
    [Parameter(Mandatory = $true)] [Guid] $StationId,
    [Parameter(Mandatory = $true)] [string] $EnrollmentToken,
    [string] $PrinterName = '',
    [ValidateSet('windows-spooler', 'fake')] [string] $Adapter = 'windows-spooler',
    [string] $ServiceName = 'NubArcaPrintAgent'
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

$installDirectory = $PSScriptRoot
$executable = Join-Path $installDirectory 'NubArca.PrintAgent.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "NubArca.PrintAgent.exe was not found in $installDirectory"
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service $ServiceName already exists. Uninstall it before reinstalling."
}

$dataDirectory = Join-Path $env:ProgramData 'NubArca\PrintAgent'
New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
& icacls.exe $dataDirectory /inheritance:r `
    /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Unable to restrict permissions on $dataDirectory" }
$configuredPrinter = if ($PrinterName) { $PrinterName } else { $null }
$configuration = @{
    PrintAgent = @{
        ServerOrigin = $ServerOrigin.TrimEnd('/')
        CredentialPath = Join-Path $dataDirectory 'credential.bin'
        JournalPath = Join-Path $dataDirectory 'journal.db'
        TemporaryPath = Join-Path $dataDirectory 'temp'
        Adapter = $Adapter
        PrinterName = $configuredPrinter
        FakeOutputPath = Join-Path $dataDirectory 'fake-output'
        IdlePollSeconds = 5
        MaxBackoffSeconds = 60
        MaxArtifactBytes = 33554432
        MaxTemporaryBytes = 134217728
    }
}
$configPath = Join-Path $installDirectory 'appsettings.Production.json'
$configuration | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM

Push-Location $installDirectory
try {
    & $executable enroll --server $ServerOrigin --station $StationId --token $EnrollmentToken
    if ($LASTEXITCODE -ne 0) { throw "Enrollment failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

$service = New-Service -Name $ServiceName -DisplayName 'NubArca Print Agent' `
    -Description 'Headless NubArca print delivery station.' `
    -BinaryPathName ('"{0}"' -f $executable) -StartupType Automatic -DependsOn Spooler
$service | Start-Service
Write-Host "NubArca Print Agent installed and started as $ServiceName."
Write-Host "Runtime data: $dataDirectory"
