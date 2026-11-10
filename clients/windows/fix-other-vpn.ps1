<#
.SYNOPSIS
Makes Netlane work while another VPN holds the default route.

.DESCRIPTION
The engine sends its own traffic out of the physical adapter, so it needs a route there.
When another VPN (L2TP, WireGuard, another client) owns the default route, that adapter
has no route to the endpoint: the client either fails with "Number of connection
attempts exceeded", or it connects and leaves the rest of the internet without a path.

This lowers the interface metric of the physical adapter below the other VPN's, which
hands the default route back to it. The other VPN keeps working for the networks it
routes. The previous metric is saved next to this script and put back by -Restore.

Administrator rights are required; the script asks for them itself.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\fix-other-vpn.ps1

.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\fix-other-vpn.ps1 -Restore
#>
param(
    [switch]$Restore,
    [int]$Metric = 10
)
$ErrorActionPreference = 'Stop'
$statePath = Join-Path $PSScriptRoot 'netlane-other-vpn-fix.json'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    Write-Host 'Requesting administrator rights...'
    $arguments = @('-NoProfile', '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
    if ($Restore) { $arguments += '-Restore' }
    if ($PSBoundParameters.ContainsKey('Metric')) { $arguments += @('-Metric', $Metric) }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs
    return
}

# The physical link that carries the real internet: Ethernet or Wi-Fi, up, with an IPv4 gateway.
# Virtual switches and tunnels have no gateway here.
function Get-PhysicalAdapter {
    $best = $null
    $bestIndex = [int]::MaxValue
    foreach ($nic in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($nic.OperationalStatus -ne [System.Net.NetworkInformation.OperationalStatus]::Up) { continue }
        $type = $nic.NetworkInterfaceType
        if ($type -ne 'Ethernet' -and $type -ne 'Wireless80211' -and $type -ne 'GigabitEthernet') { continue }
        $hasGateway = $false
        foreach ($item in $nic.GetIPProperties().GatewayAddresses) {
            if ($item.Address -ne $null -and $item.Address.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork -and -not $item.Address.Equals([System.Net.IPAddress]::Any)) { $hasGateway = $true }
        }
        if (-not $hasGateway) { continue }
        $properties = $nic.GetIPProperties().GetIPv4Properties()
        if ($properties -eq $null) { continue }
        if ($properties.Index -lt $bestIndex) { $best = $nic; $bestIndex = $properties.Index }
    }
    return $best
}

function Get-InterfaceMetric([int]$index) {
    $row = Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -ErrorAction SilentlyContinue
    if ($row) { return [int]$row.InterfaceMetric }
    return $null
}

function Show-DefaultRoutes([string]$title) {
    Write-Host ''
    Write-Host $title
    & route.exe print 0.0.0.0 | Select-String -Pattern '0\.0\.0\.0\s+0\.0\.0\.0' | ForEach-Object { Write-Host ('  ' + $_.Line) }
}

$adapter = Get-PhysicalAdapter
if ($adapter -eq $null) { throw 'No physical adapter with an IPv4 gateway was found.' }
$index = $adapter.GetIPProperties().GetIPv4Properties().Index
Write-Host ('Physical adapter: {0} (index {1})' -f $adapter.Name, $index)

if ($Restore) {
    if (-not (Test-Path -LiteralPath $statePath)) {
        Write-Host ('Nothing to restore: ' + $statePath + ' is missing.')
        Write-Host ('Set it by hand with: netsh interface ipv4 set interface "' + $adapter.Name + '" metric=automatic')
        exit 1
    }
    $saved = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    & netsh.exe interface ipv4 set interface $adapter.Name ('metric=' + $saved.Metric)
    if ($LASTEXITCODE -ne 0) { throw 'netsh failed to restore the metric.' }
    Remove-Item -LiteralPath $statePath -Force
    Write-Host ('Metric restored to ' + $saved.Metric + '.')
    Show-DefaultRoutes 'Default routes now:'
    return
}

$current = Get-InterfaceMetric $index
if ($current -eq $null) { throw 'Could not read the current interface metric.' }
if ($current -le $Metric) {
    Write-Host ('The metric is already ' + $current + '; nothing to change.')
} else {
    [IO.File]::WriteAllText($statePath, ('{"Metric":' + $current + '}'), [Text.UTF8Encoding]::new($false))
    & netsh.exe interface ipv4 set interface $adapter.Name ('metric=' + $Metric)
    if ($LASTEXITCODE -ne 0) { throw 'netsh failed to set the metric.' }
    Write-Host ('Metric changed from ' + $current + ' to ' + $Metric + '.')
}
Show-DefaultRoutes 'Default routes now:'
Write-Host ''
Write-Host 'Start Netlane and connect. General traffic now leaves through the physical adapter,'
Write-Host 'and the other VPN keeps the networks it routes. To undo this, run the script with -Restore.'
