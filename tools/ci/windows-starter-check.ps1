# Windows check of the server starter (CI: .github/workflows/ci.yml, job starter-windows).
# After start.bat installed a server into $Dir: checks the files and settings it wrote, the Windows Firewall rules,
# then starts the server once and stops it after "Done", and checks the log: the plugin enabled, the headset link and
# LAN discovery are up, and the plugin found the (stand-in) Minecraft folder under %APPDATA% and built pack.zip.
param([Parameter(Mandatory = $true)][string]$Dir, [int]$Minutes = 10)
$ErrorActionPreference = 'Stop'
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what"; $script:failed = $true } }
$failed = $false

foreach ($f in 'paper.jar', 'eula.txt', 'server.properties', 'tabletop-starter.conf', 'plugins\Geyser-Spigot.jar', 'plugins\floodgate-spigot.jar', 'plugins\TabletopCraftAR\config.yml') {
    Check (Test-Path -LiteralPath (Join-Path $Dir $f)) "file $f"
}
$jar = Get-ChildItem -LiteralPath (Join-Path $Dir 'plugins') -Filter 'tabletop-craft-ar-plugin-*.jar' | Select-Object -First 1
Check ($null -ne $jar) "plugin jar ($($jar.Name))"
Check ([bool](Select-String -LiteralPath (Join-Path $Dir 'eula.txt') -Pattern '^eula=true' -Quiet)) 'eula.txt says eula=true'
Check ([bool](Select-String -LiteralPath (Join-Path $Dir 'server.properties') -Pattern '^white-list=true' -Quiet)) 'whitelist on (answered no to an open server)'
Check ([bool](Select-String -LiteralPath (Join-Path $Dir 'plugins\TabletopCraftAR\config.yml') -Pattern '^  require-pairing: true' -Quiet)) 'config.yml requires pairing'
Check ([bool](Select-String -LiteralPath (Join-Path $Dir 'tabletop-starter.conf') -Pattern 'STARTER_BEDROCK=yes' -Quiet)) 'Bedrock choice saved'
$rules = @(Get-NetFirewallRule -DisplayName 'Tabletop Craft AR*' -ErrorAction SilentlyContinue)
$rules | Format-Table DisplayName, Enabled, Direction, Profile -AutoSize | Out-String | Write-Host
Check ($rules.Count -ge 3) "firewall rules ($($rules.Count))"

# Start the server once: stop it after "Done", or give up after $Minutes.
$java = (Get-Command java).Source
$psi = New-Object Diagnostics.ProcessStartInfo $java, '-Xms512M -Xmx2G -jar paper.jar --nogui'
$psi.WorkingDirectory = $Dir; $psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$p = [Diagnostics.Process]::Start($psi)
$err = $p.StandardError.ReadToEndAsync()
$log = New-Object Text.StringBuilder
$deadline = (Get-Date).AddMinutes($Minutes); $stopped = $false
while (-not $p.StandardOutput.EndOfStream) {
    $line = $p.StandardOutput.ReadLine()
    [void]$log.AppendLine($line); Write-Host $line
    if (-not $stopped -and $line -match 'Done \(') {
        Start-Sleep -Seconds 5   # let the asset builder finish its first file
        $p.StandardInput.WriteLine('tabletop status'); $p.StandardInput.WriteLine('stop'); $stopped = $true
    }
    if ((Get-Date) -gt $deadline) { Write-Host 'timed out: killing the server'; $p.Kill(); break }
}
$p.WaitForExit(120000) | Out-Null
$text = $log.ToString() + $err.Result
Check $stopped 'server reached Done and was stopped'
Check ($text -match 'Enabling TabletopCraftAR v') 'plugin enabled'
Check ($text -match 'Link listening on ws://') 'headset link listening'
Check ($text -match 'LAN discovery on UDP') 'LAN discovery up'
Check ($text -match 'Geyser') 'Geyser loaded'
$appMc = [Regex]::Escape((Join-Path $env:APPDATA '.minecraft'))
Check ($text -match "ASSETS auto: building .*$appMc") 'plugin found the Minecraft folder under %APPDATA%'
Check ($text -match 'ASSETS auto: pack\.zip') 'pack.zip built from it'
if ($failed) { exit 1 }
Write-Host 'Windows starter check passed.'
