param(
    [Parameter(Mandatory = $true)][string] $SearchRoot,
    [Parameter(Mandatory = $true)][string] $ExeName,
    [Parameter(Mandatory = $true)][int] $MinimumTests
)

$matches = @(Get-ChildItem -Recurse $SearchRoot -Filter $ExeName |
    Where-Object { $_.FullName -match '[\\/]Release[\\/]' })
if ($matches.Count -ne 1) {
    throw "Expected 1 Release $ExeName under $SearchRoot, found $($matches.Count): $($matches.FullName -join ', ')"
}

$exe = $matches[0].FullName
Write-Host "Running $exe"
$output = & $exe -noLogo -automated
$code = $LASTEXITCODE
$output | ForEach-Object { $_ }

if ($code -ne 0) {
    exit $code
}

$finished = $output |
    Where-Object { $_ -match '"test-assembly-finished"' } |
    Select-Object -Last 1
if (-not $finished) {
    throw "xUnit produced no test-assembly-finished line."
}

if ($finished -notmatch '"TestsTotal":(\d+)') {
    throw "Could not parse TestsTotal from: $finished"
}

$total = [int]$Matches[1]
if ($total -lt $MinimumTests) {
    throw "TestsTotal=$total is below minimum $MinimumTests"
}

Write-Host "TestsTotal=$total (minimum $MinimumTests)"
