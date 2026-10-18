<#
Tabletop Craft AR server starter for Windows (double-click start.bat, or run this in PowerShell 5.1 or 7).
It downloads, never redistributes:
  - Paper (the Minecraft server) for the supported game version, from PaperMC's own download service (checked by SHA-256);
  - the newest TabletopCraftAR plugin release from GitHub (checked by SHA-256);
  - optionally Geyser and Floodgate (Bedrock players) from GeyserMC's download service (checked by SHA-256);
asks you to accept the Minecraft EULA (you must answer: nothing accepts it for you), writes sensible settings on the
first run, offers the Windows Firewall rules, and starts the server in this window. Run it again to update Paper and
the plugin. docs/QUEST_SETUP.md "Store install" has the whole setup.

  start.bat [-Dir DIR] [-Port 25565] [-WsPort 8765] [-Memory 4G] [-Bedrock yes|no] [-NoStart] [-PluginJar PATH] [-Java PATH]
#>
param(
    [string]$Dir = (Join-Path $HOME 'TabletopCraftServer'),
    [int]$Port = 25565,
    [int]$WsPort = 8765,
    [string]$Memory = '4G',
    [string]$Bedrock = '',
    [string]$PluginJar = '',
    [string]$Java = '',
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is much faster without the progress bar (PowerShell 5.1)
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

$Repo = 'NickStassen/tabletop-craft-ar'
$DefaultMc = '26.3'      # the game version the plugin supports when its jar doesn't say
$JavaMajor = 25
$UA = "tabletop-craft-ar-starter/1.1 (+https://github.com/$Repo)"
$OnWindows = ($env:OS -eq 'Windows_NT')

function Say([string]$s) { Write-Host $s }
function Die([string]$s) { Write-Host "ERROR: $s" -ForegroundColor Red; exit 1 }
function Ask([string]$q, [bool]$def) {
    $suffix = if ($def) { '[Y/n]' } else { '[y/N]' }
    $a = Read-Host "$q $suffix"
    if ($null -eq $a -or $a.Trim() -eq '') { return $def }
    return @('y', 'yes') -contains $a.Trim().ToLower()
}
function Get-Json([string]$url) { Invoke-RestMethod -Uri $url -UserAgent $UA -Headers @{ Accept = 'application/json' } }
function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLower() }
function Save-Checked([string]$url, [string]$sha, [string]$dest) {
    $tmp = "$dest.part"
    try { Invoke-WebRequest -Uri $url -UserAgent $UA -OutFile $tmp -UseBasicParsing }
    catch { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue; Die "download failed: $url ($($_.Exception.Message))" }
    if ($sha -and (Get-Sha256 $tmp) -ne $sha.ToLower()) { Remove-Item -LiteralPath $tmp; Die "checksum mismatch for $url" }
    Move-Item -LiteralPath $tmp -Destination $dest -Force
}
function Read-ZipText([string]$zip, [string]$entry) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $e = $z.GetEntry($entry)
        if ($null -eq $e) { return $null }
        $r = New-Object IO.StreamReader($e.Open())
        try { return $r.ReadToEnd() } finally { $r.Dispose() }
    } finally { $z.Dispose() }
}

# ---- Java 25 ----
function Find-Java {
    if ($Java) { return $Java }
    $cmd = Get-Command java -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($root in @("$env:ProgramFiles\Eclipse Adoptium", "$env:ProgramFiles\Java", "$env:ProgramFiles\Microsoft")) {
        if (Test-Path $root) {
            $j = Get-ChildItem -Path $root -Filter java.exe -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match "\\(jdk|jre)-?$JavaMajor" } | Select-Object -First 1
            if ($j) { return $j.FullName }
        }
    }
    return $null
}
function Get-JavaMajor([string]$exe) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $v = (& $exe -version 2>&1 | Select-Object -First 1 | Out-String)
    $ErrorActionPreference = $old
    if ($v -match 'version "(\d+)') { return [int]$Matches[1] }
    return 0
}
$javaExe = Find-Java
if (-not $javaExe -or (Get-JavaMajor $javaExe) -lt $JavaMajor) {
    Say "Java $JavaMajor is needed to run the server."
    if ($OnWindows -and (Get-Command winget -ErrorAction SilentlyContinue) -and (Ask "Install Eclipse Temurin $JavaMajor (a free Java) with winget now?" $true)) {
        winget install --id "EclipseAdoptium.Temurin.$JavaMajor.JRE" -e --accept-source-agreements
        $javaExe = Find-Java
    }
    if (-not $javaExe -or (Get-JavaMajor $javaExe) -lt $JavaMajor) {
        Die "Install a Java $JavaMajor runtime (https://adoptium.net/temurin/releases/?version=$JavaMajor), then run this again (or pass -Java PATH)."
    }
}
Say "Java $(Get-JavaMajor $javaExe): ok ($javaExe)"

New-Item -ItemType Directory -Force -Path (Join-Path $Dir 'plugins') | Out-Null
$Dir = (Resolve-Path $Dir).Path
Set-Location $Dir
$conf = Join-Path $Dir 'tabletop-starter.conf'
$saved = @{}
if (Test-Path $conf) { foreach ($l in Get-Content $conf) { if ($l -match '^(\w+)=(.*)$') { $saved[$Matches[1]] = $Matches[2] } } }
$first = -not (Test-Path 'server.properties')

# ---- the plugin (newest GitHub release, or -PluginJar) ----
if ($PluginJar) {
    if (-not (Test-Path $PluginJar)) { Die "-PluginJar $PluginJar not found" }
    $name = Split-Path $PluginJar -Leaf
    if ($name -notmatch '^tabletop-craft-ar-plugin-.*\.jar$') { $name = 'tabletop-craft-ar-plugin-local.jar' }
    Get-ChildItem plugins -Filter 'tabletop-craft-ar-plugin-*.jar' | Remove-Item
    Copy-Item -LiteralPath $PluginJar -Destination (Join-Path 'plugins' $name)
    Say "Plugin: $name (from $PluginJar)"
} else {
    try { $rel = Get-Json "https://api.github.com/repos/$Repo/releases/latest" }
    catch { Die "can't reach GitHub to find the plugin (https://github.com/$Repo/releases)" }
    $asset = $rel.assets | Where-Object { $_.name -match '^tabletop-craft-ar-plugin-[0-9][0-9.]*\.jar$' } | Select-Object -First 1
    if (-not $asset) { Die "the newest release has no tabletop-craft-ar-plugin-<version>.jar (https://github.com/$Repo/releases)" }
    $name = $asset.name
    $sha = ''
    if ($asset.digest) { $sha = ($asset.digest -replace '^sha256:', '') }
    $dest = Join-Path 'plugins' $name
    if ((Test-Path $dest) -and (-not $sha -or (Get-Sha256 $dest) -eq $sha)) { Say "Plugin: $name (up to date)" }
    else {
        Say "Plugin: downloading $name ..."
        Save-Checked $asset.browser_download_url $sha "$dest.new"
        Get-ChildItem plugins -Filter 'tabletop-craft-ar-plugin-*.jar' | Remove-Item
        Move-Item -LiteralPath "$dest.new" -Destination $dest -Force
    }
}
$plugin = Join-Path (Join-Path $Dir 'plugins') $name

# ---- the game version the plugin supports (its plugin.yml api-version), then Paper for it ----
$mc = $DefaultMc
$yml = Read-ZipText $plugin 'plugin.yml'
if ($yml -and $yml -match "(?m)^api-version:\s*'?([0-9.]+)") { $mc = $Matches[1] }
try { $builds = Get-Json "https://fill.papermc.io/v3/projects/paper/versions/$mc/builds" }
catch { Die "can't reach PaperMC's download service for Minecraft $mc" }
$stable = $builds | Where-Object { $_.channel -eq 'STABLE' } | Select-Object -First 1
if (-not $stable) { Die "PaperMC has no stable build for Minecraft $mc yet" }
$dl = $stable.downloads.'server:default'
if ((Test-Path 'paper.jar') -and (Get-Sha256 'paper.jar') -eq $dl.checksums.sha256) { Say "Paper $mc build $($stable.id): up to date" }
else { Say "Paper $mc build $($stable.id): downloading ..."; Save-Checked $dl.url $dl.checksums.sha256 'paper.jar' }

# ---- Geyser + Floodgate (optional) ----
if (-not $Bedrock) { $Bedrock = $saved['STARTER_BEDROCK'] }
if (-not $Bedrock) {
    $Bedrock = 'no'
    if (Ask 'Let Bedrock players (phones, consoles, Windows) join too? Installs Geyser and Floodgate from GeyserMC' $false) { $Bedrock = 'yes' }
}
if ($Bedrock -eq 'yes') {
    foreach ($p in @('geyser', 'floodgate')) {
        $info = Get-Json "https://download.geysermc.org/v2/projects/$p/versions/latest/builds/latest"
        $file = if ($p -eq 'geyser') { 'plugins\Geyser-Spigot.jar' } else { 'plugins\floodgate-spigot.jar' }
        $file = $file -replace '\\', [IO.Path]::DirectorySeparatorChar
        $gsha = $info.downloads.spigot.sha256
        if ((Test-Path $file) -and (Get-Sha256 $file) -eq $gsha) { Say "$p $($info.version) build $($info.build): up to date" }
        else {
            Say "$p $($info.version) build $($info.build): downloading ..."
            Save-Checked "https://download.geysermc.org/v2/projects/$p/versions/$($info.version)/builds/$($info.build)/downloads/spigot" $gsha $file
        }
    }
}
Set-Content -Path $conf -Value "STARTER_BEDROCK=$Bedrock"

# ---- the Minecraft EULA: only you can accept it ----
if (-not ((Test-Path 'eula.txt') -and (Select-String -Path 'eula.txt' -Pattern '^eula=true' -Quiet))) {
    Say ''
    Say "Minecraft's End User License Agreement applies to running a server: https://aka.ms/MinecraftEULA"
    if (Ask 'Have you read and do you accept the Minecraft EULA?' $false) {
        Set-Content -Path 'eula.txt' -Value @("# Accepted by the user in the Tabletop Craft AR starter on $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))", 'eula=true')
    } else {
        Say "Not accepted: the server can't start. Run this again when you want to accept it."
        exit 1
    }
}

# ---- first run: server and plugin settings ----
if ($first) {
    Say ''
    Say 'Java players: by default only players you add to the whitelist can join (type: whitelist add <name>).'
    $open = Ask 'Let any Java player who can reach this PC join instead (no whitelist)?' $false
    $wl = if ($open) { 'false' } else { 'true' }
    Set-Content -Path 'server.properties' -Value @('motd=Tabletop Craft AR', "server-port=$Port", "white-list=$wl", "enforce-whitelist=$wl")
    $cfgDir = Join-Path 'plugins' 'TabletopCraftAR'
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    $cfg = Join-Path $cfgDir 'config.yml'
    if (-not (Test-Path $cfg)) {
        # The plugin's own defaults, with: headsets must be paired first, and the chosen link port.
        $text = Read-ZipText $plugin 'config.yml'
        if ($text) {
            $text = $text -replace '(?m)^  require-pairing: false\r?$', '  require-pairing: true' -replace '(?m)^  port: 8765\r?$', "  port: $WsPort"
        } else {
            $text = "auth:`n  require-pairing: true`nbind:`n  port: $WsPort`ndiscovery:`n  port: $WsPort`n"
        }
        [IO.File]::WriteAllText((Join-Path $Dir $cfg), $text)
    }
}
$cfgPath = Join-Path (Join-Path (Join-Path $Dir 'plugins') 'TabletopCraftAR') 'config.yml'
$wsp = $WsPort
if (Test-Path $cfgPath) { $m = Select-String -Path $cfgPath -Pattern '^  port: (\d+)$' | Select-Object -First 1; if ($m) { $wsp = $m.Matches[0].Groups[1].Value } }
$mcPort = $Port
$pm = Select-String -Path 'server.properties' -Pattern '^server-port=(\d+)' | Select-Object -First 1
if ($pm) { $mcPort = $pm.Matches[0].Groups[1].Value }

# ---- Windows Firewall: the headset needs TCP and UDP to the link port ----
if ($OnWindows -and (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue)) {
    $have = Get-NetFirewallRule -DisplayName 'Tabletop Craft AR*' -ErrorAction SilentlyContinue
    if (-not $have -and (Ask "Allow the headset through Windows Firewall (TCP+UDP $wsp, and TCP $mcPort for Java players; private networks only)? Asks for administrator rights" $true)) {
        $rules = "New-NetFirewallRule -DisplayName 'Tabletop Craft AR link (TCP)' -Direction Inbound -Protocol TCP -LocalPort $wsp -Action Allow -Profile Private; " +
                 "New-NetFirewallRule -DisplayName 'Tabletop Craft AR link (UDP)' -Direction Inbound -Protocol UDP -LocalPort $wsp -Action Allow -Profile Private; " +
                 "New-NetFirewallRule -DisplayName 'Tabletop Craft AR Minecraft (TCP)' -Direction Inbound -Protocol TCP -LocalPort $mcPort -Action Allow -Profile Private"
        try { Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -ArgumentList @('-NoProfile', '-Command', $rules) }
        catch { Say "Firewall rules not added ($($_.Exception.Message)). Add them by hand if the headset can't find the server." }
    }
}

# ---- Windows network profile: the rules above apply to Private networks, and Windows often calls a home network Public ----
if ($OnWindows -and (Get-Command Get-NetConnectionProfile -ErrorAction SilentlyContinue)) {
    $public = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object { "$($_.NetworkCategory)" -eq 'Public' })
    if ($public.Count -gt 0) {
        Say ''
        foreach ($n in $public) { Say "Note: Windows treats the network on '$($n.InterfaceAlias)' as Public, so its firewall blocks the headset there." }
        Say '  If that is your home network: Settings > Network & internet > (your Wi-Fi or Ethernet) > Network profile type: Private.'
    }
}

# ---- what to do next ----
$ip = ''
if (Get-Command Get-NetIPAddress -ErrorAction SilentlyContinue) {
    $a = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.InterfaceAlias -notmatch 'vEthernet|Loopback|VirtualBox|VMware' } |
        Select-Object -First 1
    if ($a) { $ip = $a.IPAddress }
}
if (-not $ip) { $ip = '<this PC''s LAN address>' }
Say ''
Say "Server folder: $Dir"
Say "This PC on your network: $ip  (Java players: ${ip}:$mcPort)"
Say "Headsets find it by themselves on the same Wi-Fi (TCP and UDP $wsp must be allowed through the firewall)."
Say "Start Minecraft: Java Edition $mc once in the launcher on this PC: the server sends its textures and sounds to"
Say 'your paired headsets. When the headset shows a 6-digit code, type in this window:  tabletop pair <code>'
Say ''
if ($NoStart) { Say 'Installed (-NoStart).'; exit 0 }
Say 'Starting the server (type stop to end it) ...'
& $javaExe -Xms1G "-Xmx$Memory" -jar paper.jar --nogui
