# Opens the server's ports in Windows Firewall for players on other PCs (LAN, internet, VPS):
#   TCP <port>                    the backend (server.json "port", default 6969)
#   UDP <portStart>-<portEnd>     the raids' game servers (server.json matchmaking, default 7777-7796)
# Run "Open firewall ports.bat" (asks for administrator rights). Running it again replaces the rules.
# Remove them with: open_firewall_ports.ps1 -Remove
# A router still needs the same ports forwarded to this PC; a VPS provider's own firewall / security group must allow
# them as well (that one is set on the provider's website, not in Windows).
param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$tcpName = 'EFT Alpha Server (backend TCP)'
$udpName = 'EFT Alpha Server (raids UDP)'

$port = 6969; $start = 7777; $end = 7796
$cfg = Join-Path $PSScriptRoot 'Files\configs\server.json'
if (-not (Test-Path $cfg)) { $cfg = Join-Path $PSScriptRoot 'Files\configs\server_base.json' }
if (Test-Path $cfg) {
    try {
        $json = Get-Content -Raw -LiteralPath $cfg | ConvertFrom-Json
        if ($json.port) { $port = [int]$json.port }
        if ($json.matchmaking) {
            if ($json.matchmaking.portStart) { $start = [int]$json.matchmaking.portStart }
            if ($json.matchmaking.portEnd) { $end = [int]$json.matchmaking.portEnd }
        }
    } catch { Write-Host "Could not read $cfg, using the default ports: $_" }
}
if ($end -lt $start) { $end = $start }
$hostPort = 7777
if ($json -and $json.matchmaking -and $json.matchmaking.hostDefaultPort) { $hostPort = [int]$json.matchmaking.hostDefaultPort }
$range = if ($start -eq $end) { "$start" } else { "$start-$end" }
# the default host port too when outside the range (same list as the server's check and the launcher)
if ($hostPort -gt 0 -and ($hostPort -lt $start -or $hostPort -gt $end)) { $range += ",$hostPort" }

foreach ($name in $tcpName, $udpName) {
    & netsh advfirewall firewall delete rule name="$name" | Out-Null
}
if ($Remove) { Write-Host 'Firewall rules removed.'; exit 0 }

& netsh advfirewall firewall add rule name="$tcpName" dir=in action=allow protocol=TCP localport=$port profile=any | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not add the TCP rule (run as administrator)." }
& netsh advfirewall firewall add rule name="$udpName" dir=in action=allow protocol=UDP localport=$range profile=any | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not add the UDP rule (run as administrator)." }
Write-Host "Windows Firewall now allows TCP $port (backend) and UDP $range (raids) on all networks."
Write-Host 'Players from the internet also need these ports forwarded on the router (or allowed in the VPS provider''s firewall).'
