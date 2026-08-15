# Detect the IPv4 address of the adapter that currently has the
# default route (the network this PC is actually connected to).
$ErrorActionPreference = 'SilentlyContinue'
$index = Get-NetRoute -DestinationPrefix '0.0.0.0/0' |
    Sort-Object RouteMetric |
    Select-Object -First 1 -ExpandProperty InterfaceIndex
if (-not $index) { exit 0 }
$ip = Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $index |
    Where-Object { $_.IPAddress -notlike '169.254.*' } |
    Select-Object -First 1 -ExpandProperty IPAddress
if ($ip) { Write-Output $ip }
exit 0
