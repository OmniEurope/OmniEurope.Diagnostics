# ylaunch-core 1.0.7
#
# Shared launcher core. Everything project-specific lives in the repository's own ylaunch.ps1 ($LaunchConfig) and in the
# gitignored .ylaunch.local.
#
# Requires PowerShell 7.2+. The root ylaunch.ps1 relaunches Windows PowerShell into pwsh before it
# dot-sources this file, because PS 5.1 parses the whole file before running any of it.
#
# Canonical flags: -s Silent, -r Reset,
# -t unit suites, -ta everything, -tb/-tf/-ti/-te/-tec, -c Coverage, -hr HotReload, -w Worktree,
# -cdp DebugPort (desktop host), -h/-hl. No test flag ever resets the database: only -r does.
#
# 1.0.1: $LaunchConfig.OwnedFolders (default @('src')) replaces the hard-coded src\ ownership prefix;
# every component runs in its own project folder (WorkingDirectory to override); test flags that
# start no application skip the occupied-port check; a config with both Web and Maui starts the web
# components first, then the desktop app, and stops them together.

# 1.0.6: MSBuild node reuse is off for the whole run (MSBUILDDISABLENODEREUSE=1, the caller's value put back
# on return). A reused node may belong to another session, possibly sandboxed without write access to this
# checkout, and every build of the run then failed with "Access to the path ... is denied".

Set-StrictMode -Off

$script:YLaunchCoreVersion = '1.0.7'
$script:YLaunchExitCode = 0
# Components started by this run, so an unexpected error still stops them instead of orphaning them.
$script:YActiveJobs = $null

# ============================================================
#  Output helpers
# ============================================================

function Write-YStep([string]$Message) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  $Message" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
}

function Write-YFail([string]$Message) {
    Write-Host $Message -ForegroundColor Red
    $script:YLaunchExitCode = 1
}

function Get-YOption([hashtable]$Options, [string]$Name) {
    if (-not $Options.ContainsKey($Name)) { return $null }
    $value = $Options[$Name]
    if ($value -is [System.Management.Automation.SwitchParameter]) { return $value.IsPresent }
    return $value
}

# Keys of an optional configuration hashtable: an absent one yields nothing, never a $null key.
function Get-YKeys($Table) {
    if ($Table -is [System.Collections.IDictionary]) { return @($Table.Keys) }
    return @()
}

function Test-YInteractive {
    return -not $env:CI -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected
}

# ============================================================
#  Seed accounts (development logins shown at startup)
# ============================================================

# $LaunchConfig.SeedAccounts lists the accounts the application seeds in development, so a start
# prints how to log in. Each entry has a Login and exactly one password source:
#   Password    = the literal development value the seeder uses (mirror of the seeder's code);
#   PasswordKey = a configuration key read like ASP.NET Development does, highest precedence first:
#                 environment variable (':' -> '__'), user secrets of Project, then Project's
#                 appsettings.Development.json and appsettings.json.
# Optional: Role, Note. A key found nowhere prints "not found": a password is never invented.
function Test-YSeedAccounts([hashtable]$Config) {
    foreach ($a in @($Config.SeedAccounts | Where-Object { $_ })) {
        if ($a -isnot [hashtable] -or [string]::IsNullOrWhiteSpace([string]$a.Login)) { throw "Every SeedAccounts entry needs a Login: @{ Login = 'admin'; Password = '...' }." }
        $sources = @(@('Password', 'PasswordKey') | Where-Object { $a.ContainsKey($_) }).Count
        if ($sources -ne 1) { throw "Seed account '$($a.Login)' needs exactly one of Password or PasswordKey." }
        if ($a.ContainsKey('PasswordKey') -and [string]::IsNullOrWhiteSpace([string]$a.Project)) { throw "Seed account '$($a.Login)': PasswordKey needs the Project that reads it." }
    }
}

function Get-YJsonKey([string]$File, [string]$Key) {
    if (-not (Test-Path -LiteralPath $File)) { return $null }
    try { $node = Get-Content -LiteralPath $File -Raw | ConvertFrom-Json -AsHashtable } catch { return $null }
    foreach ($part in $Key.Split(':')) {
        if ($node -isnot [System.Collections.IDictionary]) { return $null }
        $match = @($node.Keys | Where-Object { $_ -ieq $part }) | Select-Object -First 1
        if ($null -eq $match) { return $null }
        $node = $node[$match]
    }
    if ($node -is [string] -and $node) { return $node }
    return $null
}

function Resolve-YSeedPassword([hashtable]$Account, [string]$Root, [hashtable]$SecretsCache) {
    if ($Account.ContainsKey('Password')) { return @{ Value = [string]$Account.Password; Source = 'launcher config' } }
    $key = [string]$Account.PasswordKey
    $fromEnv = [Environment]::GetEnvironmentVariable($key.Replace(':', '__'))
    if ($fromEnv) { return @{ Value = $fromEnv; Source = "env $($key.Replace(':', '__'))" } }
    $project = Resolve-YPath $Root $Account.Project
    if (-not $SecretsCache.ContainsKey($project)) {
        $pairs = @{}
        if (Test-Path -LiteralPath $project) {
            foreach ($line in @(& dotnet user-secrets list --project $project 2>$null)) {
                if ("$line" -match '^\s*(.+?)\s+=\s+(.*)$') { $pairs[$Matches[1]] = $Matches[2] }
            }
        }
        $SecretsCache[$project] = $pairs
    }
    $secret = @($SecretsCache[$project].Keys | Where-Object { $_ -ieq $key }) | Select-Object -First 1
    if ($secret) { return @{ Value = [string]$SecretsCache[$project][$secret]; Source = 'user secrets' } }
    $folder = Split-Path -Parent $project
    foreach ($name in 'appsettings.Development.json', 'appsettings.json') {
        $value = Get-YJsonKey (Join-Path $folder $name) $key
        if ($value) { return @{ Value = $value; Source = $name } }
    }
    return $null
}

function Show-YSeedAccounts([hashtable]$Config, [string]$Root) {
    $accounts = @($Config.SeedAccounts | Where-Object { $_ })
    if ($accounts.Count -eq 0) { return }
    $cache = @{}
    Write-Host ""
    Write-Host "Seed accounts (development):" -ForegroundColor Cyan
    foreach ($a in $accounts) {
        $resolved = Resolve-YSeedPassword $a $Root $cache
        $role = if ($a.Role) { "  [$($a.Role)]" } else { '' }
        if ($resolved) {
            Write-Host ("  {0}  /  {1}{2}" -f $a.Login, $resolved.Value, $role) -ForegroundColor White -NoNewline
            Write-Host "  ($($resolved.Source))" -ForegroundColor DarkGray
        } else {
            Write-Host ("  {0}  /  password not found (key {1}: env, user secrets, appsettings){2}" -f $a.Login, $a.PasswordKey, $role) -ForegroundColor Yellow
        }
        if ($a.Note) { Write-Host "      $($a.Note)" -ForegroundColor DarkGray }
    }
}

# ============================================================
#  Declared exceptions
# ============================================================

# $LaunchConfig.Exceptions declares a launcher capability the project deliberately does not have, with
# its reason: @{ E2E = 'component library, no application to drive' }. Returns the reason, or $null
# when nothing is declared. A declaration that is empty, unknown or contradicted by the configuration
# is refused, so an exception cannot hide a typo or outlive the capability it excused.
$script:YKnownExceptions = @('E2E')
function Get-YDeclaredException([hashtable]$Config, [string]$Key) {
    if (-not $Config.ContainsKey('Exceptions') -or $null -eq $Config.Exceptions) { return $null }
    if ($Config.Exceptions -isnot [hashtable]) { throw "`$LaunchConfig.Exceptions must be a hashtable: @{ E2E = '<reason>' }." }
    foreach ($name in $Config.Exceptions.Keys) {
        if ($script:YKnownExceptions -notcontains $name) { throw "Unknown launcher exception '$name'. Known: $($script:YKnownExceptions -join ', ')." }
        if ([string]::IsNullOrWhiteSpace([string]$Config.Exceptions[$name])) { throw "Launcher exception '$name' has no reason." }
        if ($Config.ContainsKey($name)) { throw "Launcher exception '$name' is declared but `$LaunchConfig.$name exists: remove one of them." }
    }
    if ($Config.Exceptions.ContainsKey($Key)) { return [string]$Config.Exceptions[$Key] }
    return $null
}

# ============================================================
#  Help
# ============================================================

function Show-YHelp([hashtable]$Config, [switch]$Long) {
    $hasWeb  = $Config.ContainsKey('Web')
    $hasMaui = $Config.ContainsKey('Maui')
    $suites  = @($Config.Tests)
    Write-Host ""
    Write-Host "ylaunch.ps1 - $($Config.Name) (ylaunch-core $script:YLaunchCoreVersion)" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "USAGE:" -ForegroundColor Yellow
    Write-Host "  .\ylaunch.ps1 [OPTIONS]"
    Write-Host ""
    Write-Host "OPTIONS:" -ForegroundColor Yellow
    Write-Host "  -s,   -Silent           Start without opening a browser; a desktop app alone returns once its window is up"
    Write-Host "  -r,   -Reset            Snapshot then reset the local database (the only flag that resets it)"
    Write-Host "  -t,   -TestUnit         Run every unit suite, then exit"
    Write-Host "  -ta,  -TestAll          Run every suite: unit + integration + E2E, then exit"
    foreach ($s in $suites | Where-Object { $_.Alias }) {
        Write-Host ("  {0,-6}{1,-18}Run the {2} suite, then exit" -f "-$($s.Alias),", "-$($s.Flag)", $s.Key)
    }
    if ($Config.ContainsKey('E2E')) {
        Write-Host "  -te,  -TestE2e          Start the app, run the E2E suite, stop, exit"
        Write-Host "  -tec, -TestE2eFilter    E2E categories by number or name (? = picker), implies -te"
    }
    $e2eException = Get-YDeclaredException -Config $Config -Key 'E2E'
    if ($e2eException) { Write-Host "  (declared exception: no E2E suite, $e2eException)" -ForegroundColor DarkYellow }
    Write-Host "  -c,   -Coverage         Unit suites with coverage + HTML report, then exit"
    Write-Host "  -hr,  -HotReload        Start under dotnet watch"
    if ($hasWeb)  { Write-Host "  -w,   -Worktree         Give this checkout fresh ports (.ylaunch.local), then launch" }
    if ($hasMaui) { Write-Host "  -cdp, -DebugPort <n>    Expose the WebView2 on a CDP port (http://127.0.0.1:<n>/json/list)" }
    foreach ($line in @($Config.ExtraHelp)) { if ($line) { Write-Host "  $line" } }
    Write-Host "  -h,   -Help             Show this help"
    Write-Host "  -hl,  -HelpLong         Show this help with the workflow"
    Write-Host ""
    if (-not $Long) { return }
    Write-Host "WORKFLOW:" -ForegroundColor Yellow
    Write-Host "  1. Preflight: SDK guard and mechanised code rules (report only, never blocking)"
    $owned = (Get-YOwnedFolders $Config | ForEach-Object { "$_\" }) -join ', '
    Write-Host "  2. Stops the previous instance of THIS checkout only (image path or command line under $owned)"
    Write-Host "  3. Builds (skipped with -hr), runs the requested tests and exits on any test flag"
    Write-Host "  4. -r snapshots the database, then resets it; otherwise the database is only started"
    Write-Host "  5. Starts every component in its project folder (web first, then the desktop app), waits for"
    Write-Host "     readiness (a timeout is a failure), opens the browser"
    Write-Host "  6. Streams output; when one component stops, every component is stopped"
    Write-Host ""
    Write-Host "  .ylaunch.local (gitignored, KEY=VALUE): port keys of this checkout, LABEL, WORKTREE." -ForegroundColor DarkGray
    Write-Host "  Tests: counters come from the TRX file; a suite with no TRX or zero tests is a failure." -ForegroundColor DarkGray
    Write-Host ""
}

# ============================================================
#  Paths, global.json, .ylaunch.local
# ============================================================

function Resolve-YPath([string]$Root, [string]$Relative) {
    if (-not $Relative) { return $null }
    if ([IO.Path]::IsPathRooted($Relative)) { return [IO.Path]::GetFullPath($Relative) }
    return [IO.Path]::GetFullPath((Join-Path $Root $Relative))
}

# dotnet resolves global.json by walking up from the working directory; so does the launcher.
function Find-YGlobalJson([string]$Root) {
    $dir = [IO.DirectoryInfo]::new($Root)
    while ($dir) {
        $candidate = Join-Path $dir.FullName 'global.json'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
        $dir = $dir.Parent
    }
    return $null
}

function Get-YTestRunner([string]$Root) {
    $globalJson = Find-YGlobalJson $Root
    if ($globalJson) {
        $json = Get-Content -LiteralPath $globalJson -Raw | ConvertFrom-Json
        if ($json.PSObject.Properties['test'] -and $json.test.runner -eq 'Microsoft.Testing.Platform') { return 'MTP' }
    }
    return 'VSTest'
}

function Read-YLocalSettings([string]$Path) {
    $settings = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Path)) { return $settings }
    foreach ($line in Get-Content -LiteralPath $Path) {
        $t = $line.Trim()
        if (-not $t -or $t.StartsWith('#')) { continue }
        $i = $t.IndexOf('=')
        if ($i -lt 1) { continue }
        $settings[$t.Substring(0, $i).Trim()] = $t.Substring($i + 1).Trim()
    }
    return $settings
}

function Test-YPortFree([int]$Port) {
    $listener = $null
    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        return $true
    } catch {
        return $false
    } finally {
        if ($listener) { $listener.Stop() }
    }
}

# Every port key declared by the web components, with its committed default.
function Get-YPortKeys([hashtable]$Config) {
    $keys = [ordered]@{}
    if (-not $Config.ContainsKey('Web')) { return $keys }
    foreach ($component in @($Config.Web.Components)) {
        foreach ($url in @($component.Urls)) { $keys[$url.Key] = [int]$url.Default }
    }
    return $keys
}

# -w: a fresh, non-default port set, skipping bound ports and the ports the file already used, so a
# re-run genuinely renews them. LABEL is kept, WORKTREE is always the checkout folder name.
function New-YWorktreePorts([hashtable]$Config, [string]$Root, [string]$LocalPath, [string]$Reason) {
    $defaults = Get-YPortKeys $Config
    $existing = Read-YLocalSettings $LocalPath
    $reserved = @($defaults.Keys | Where-Object { $existing.Contains($_) } | ForEach-Object { [int]$existing[$_] })
    $picked = $null
    for ($slot = 1; $slot -le 50 -and -not $picked; $slot++) {
        $candidate = [ordered]@{}
        $ok = $true
        foreach ($key in $defaults.Keys) {
            $port = $defaults[$key] + ($slot * 10)
            if ($reserved -contains $port -or -not (Test-YPortFree $port)) { $ok = $false; break }
            $candidate[$key] = $port
        }
        if ($ok) { $picked = $candidate }
    }
    if (-not $picked) { throw "No free port set found (50 slots scanned above the defaults)." }

    $folder = Split-Path $Root -Leaf
    $label = if ($existing.Contains('LABEL') -and $existing['LABEL']) { $existing['LABEL'] } else { $folder }
    $lines = @("# Per-checkout ylaunch overrides (gitignored). Written by ylaunch.ps1 ($Reason).")
    foreach ($key in $picked.Keys) { $lines += "$key=$($picked[$key])" }
    foreach ($key in $existing.Keys) {
        if (-not $picked.Contains($key) -and $key -notin @('LABEL', 'WORKTREE')) { $lines += "$key=$($existing[$key])" }
    }
    $lines += "LABEL=$label"
    $lines += "WORKTREE=$folder"
    ($lines -join "`n") | Set-Content -LiteralPath $LocalPath -Encoding utf8
    $summary = ($picked.Keys | ForEach-Object { "$_=$($picked[$_])" }) -join ', '
    Write-Host "  .ylaunch.local written: $summary, label '$label'." -ForegroundColor Green
}

function Resolve-YRuntime([hashtable]$Config, [string]$Root, [hashtable]$Options) {
    $localPath = Join-Path $Root '.ylaunch.local'
    if ((Get-YOption $Options 'Worktree') -and $Config.ContainsKey('Web')) {
        New-YWorktreePorts -Config $Config -Root $Root -LocalPath $localPath -Reason '-w'
    }
    $local = Read-YLocalSettings $localPath
    $ports = [ordered]@{}
    $defaults = Get-YPortKeys $Config
    foreach ($key in $defaults.Keys) {
        $ports[$key] = if ($local.Contains($key)) { [int]$local[$key] } else { $defaults[$key] }
    }
    $overridden = @($defaults.Keys | Where-Object { $local.Contains($_) }).Count -gt 0
    if ($overridden) {
        Write-Host "  Ports (.ylaunch.local): $(($ports.Keys | ForEach-Object { "$_=$($ports[$_])" }) -join ', ')" -ForegroundColor Magenta
    }
    if ($local.Contains('WORKTREE')) { Write-Host "  Worktree (.ylaunch.local): $($local['WORKTREE'])" -ForegroundColor Magenta }
    return @{
        LocalPath = $localPath
        Local     = $local
        Ports     = $ports
        Label     = if ($local.Contains('LABEL')) { $local['LABEL'] } else { '' }
    }
}

function Get-YComponentUrl([hashtable]$Component, [hashtable]$Runtime, [int]$Index = 0) {
    $url = @($Component.Urls)[$Index]
    $scheme = if ($url.Scheme) { $url.Scheme } else { 'http' }
    return "${scheme}://localhost:$($Runtime.Ports[$url.Key])"
}

# Expands {PORT_KEY} and {URL:ComponentKey} placeholders in configured values.
function Expand-YValue([string]$Value, [hashtable]$Config, [hashtable]$Runtime) {
    if (-not $Value) { return $Value }
    $result = $Value
    foreach ($key in $Runtime.Ports.Keys) { $result = $result.Replace("{$key}", [string]$Runtime.Ports[$key]) }
    if ($Config.ContainsKey('Web')) {
        foreach ($component in @($Config.Web.Components)) {
            $result = $result.Replace("{URL:$($component.Key)}", (Get-YComponentUrl $component $Runtime))
        }
    }
    return $result
}

# ============================================================
#  Preflight: SDK guard + mechanised code rules (report only)
# ============================================================

function Show-YVersionGuard([string]$Root, [string]$Solution) {
    $globalJson = Find-YGlobalJson $Root
    if (-not $globalJson) {
        Write-Host "  [guard] global.json missing: the SDK floor is undeclared." -ForegroundColor Yellow
        return
    }
    $floor = (Get-Content -LiteralPath $globalJson -Raw | ConvertFrom-Json).sdk.version
    Push-Location -LiteralPath $Root
    try { $resolved = (& dotnet --version 2>$null | Select-Object -Last 1) } finally { Pop-Location }
    if (-not $resolved) {
        Write-Host "  [guard] dotnet --version failed: no installed SDK satisfies the floor $floor ($globalJson)." -ForegroundColor Yellow
        return
    }
    if ([version]($resolved -replace '-.*$', '') -lt [version]$floor) {
        Write-Host "  [guard] resolved SDK $resolved is below the global.json floor $floor." -ForegroundColor Yellow
    }
    $channel = "{0}.{1}" -f ([version]$floor).Major, ([version]$floor).Minor
    try {
        $index = Invoke-RestMethod "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json" -TimeoutSec 4
        $entry = $index.'releases-index' | Where-Object { $_.'channel-version' -eq $channel } | Select-Object -First 1
        if ($entry -and [version]$entry.'latest-sdk' -gt [version]($resolved -replace '-.*$', '')) {
            Write-Host "  [guard] SDK $($entry.'latest-sdk') is published, this checkout resolves $resolved (floor $floor)." -ForegroundColor Yellow
        } else {
            Write-Host "  SDK $resolved (floor $floor, latest published $($entry.'latest-sdk'))." -ForegroundColor DarkGray
        }
    } catch {
        Write-Host "  SDK $resolved (floor $floor, release index unreachable)." -ForegroundColor DarkGray
    }
    if ($Solution -and (Test-Path -LiteralPath $Solution)) {
        $outdated = & dotnet list $Solution package --outdated --format json 2>$null
        $scope = ''
        if ($LASTEXITCODE -ne 0) {
            # This command ignores packageSourceMapping and asks every feed for every package, and a private
            # feed answers 401 for a package it does not hold: the public feed alone still gives the answer.
            $outdated = & dotnet list $Solution package --outdated --source https://api.nuget.org/v3/index.json --format json 2>$null
            $scope = ', nuget.org only'
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  [guard] NuGet update check failed: package freshness could not be verified." -ForegroundColor Yellow
            return
        }
        try {
            $report = ($outdated -join "`n") | ConvertFrom-Json -ErrorAction Stop
            if (-not $report -or -not $report.PSObject.Properties['projects']) {
                throw 'NuGet JSON contains no projects collection.'
            }
            # Only the three compiler packages qualify, at the floor's exact pin.
            $floorVersion = [version]$floor
            $band = '{0}.{1}.{2}' -f $floorVersion.Major, $floorVersion.Minor, [math]::Floor($floorVersion.Build / 100)
            $roslynPins = @{ '9.0.1' = '4.12.0'; '10.0.1' = '5.0.0'; '10.0.2' = '5.3.0'; '10.0.3' = '5.6.0'; '10.0.4' = '5.9.0' }
            $compilerPackages = @('Microsoft.CodeAnalysis.CSharp', 'Microsoft.CodeAnalysis.CSharp.Workspaces', 'Microsoft.CodeAnalysis.Workspaces.Common')
            $count = 0
            $pinned = 0
            foreach ($project in @($report.projects)) {
                if (-not $project.PSObject.Properties['frameworks']) { continue }
                foreach ($framework in @($project.frameworks)) {
                    if (-not $framework.PSObject.Properties['topLevelPackages']) { continue }
                    foreach ($package in @($framework.topLevelPackages)) {
                        if (-not $package -or -not $package.PSObject.Properties['id'] -or -not $package.PSObject.Properties['resolvedVersion']) {
                            throw 'NuGet JSON contains an incomplete package reference.'
                        }
                        # A package of a private feed, in the nuget.org only check: no version to compare it with.
                        if ($package.PSObject.Properties['latestVersion'] -and $package.latestVersion -notmatch '^\d') { continue }
                        if ($roslynPins.ContainsKey($band) -and $package.id -in $compilerPackages -and $package.resolvedVersion -eq $roslynPins[$band]) {
                            $pinned++
                        } else {
                            $count++
                        }
                    }
                }
            }
            if ($count -gt 0) {
                Write-Host "  [guard] $count NuGet package reference(s) outdated (dotnet list package --outdated$scope)." -ForegroundColor Yellow
            }
            if ($pinned -gt 0) {
                Write-Host "  [guard] $pinned Roslyn package reference(s) deliberately held at $($roslynPins[$band]) for SDK floor $floor." -ForegroundColor DarkGray
            }
        } catch {
            Write-Host "  [guard] NuGet update check unreadable: package freshness could not be verified ($($_.Exception.Message))." -ForegroundColor Yellow
        }
    }
}

# Optional rules check, outside the repository: a folder holding verify-rules.ps1, named by
# $env:YLAUNCH_RULES_ROOT or the RULES_ROOT key of the gitignored .ylaunch.local. Without one the check
# is skipped. The launcher shows the count and never blocks.
function Show-YRulesPreflight([string]$Root) {
    $rulesRoot = $env:YLAUNCH_RULES_ROOT
    $localPath = Join-Path $Root '.ylaunch.local'
    if (-not $rulesRoot -and (Test-Path -LiteralPath $localPath)) {
        $line = Get-Content -LiteralPath $localPath | Where-Object { $_ -match '^\s*RULES_ROOT\s*=' } | Select-Object -First 1
        if ($line) { $rulesRoot = ($line -split '=', 2)[1].Trim() }
    }
    if (-not $rulesRoot) { return }
    $verifyRules = Join-Path $rulesRoot 'verify-rules.ps1'
    if (-not (Test-Path -LiteralPath $verifyRules)) {
        Write-Host "  [rules] verify-rules.ps1 not found in ${rulesRoot}: rules not checked." -ForegroundColor DarkGray
        return
    }
    try {
        $lines = @(& $verifyRules -Root $Root -Warn *>&1 | ForEach-Object { "$_" })
        $summary = $lines | Where-Object { $_ -match '^Regles mecanisees' } | Select-Object -First 1
        if ($summary) {
            $color = if ($summary -match 'aucun motif') { 'DarkGray' } else { 'Yellow' }
            Write-Host "  [rules] $summary Detail: & '$verifyRules' -Root '$Root' -Warn" -ForegroundColor $color
        } else {
            Write-Host "  [rules] verify-rules.ps1 produced no summary line." -ForegroundColor Yellow
        }
    } catch {
        Write-Host "  [rules] verify-rules.ps1 failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# ============================================================
#  Process ownership: only ever this checkout's
# ============================================================

# The single definition of "this checkout": the owned folders of $LaunchConfig.OwnedFolders under the
# checkout root, <root>\src\ by default. A bare project name would match every checkout on the
# machine, and <root> alone would let a parent checkout claim the worktrees nested under it
# (nested worktrees), so the root itself is never an owned folder. Shared toolchain processes are
# never the application.
$script:YToolchainPattern = '(?i)MSBuild\.dll|VBCSCompiler|LanguageServer|\brzc\.dll|testhost|vstest\.console|dotnet-ef|ServiceHub|Roslyn'

# Owned folders, relative to the checkout root. A flat repository lists its project folders
# (@('Themis.Back', 'Themis.Front')); an absent key keeps the standard layout's @('src').
function Get-YOwnedFolders([hashtable]$Config) {
    $folders = @()
    if ($Config -and $Config.ContainsKey('OwnedFolders')) { $folders = @($Config.OwnedFolders | Where-Object { $_ }) }
    if ($folders.Count -eq 0) { return @('src') }
    foreach ($folder in $folders) {
        $text = ([string]$folder).Trim()
        if ([IO.Path]::IsPathRooted($text) -or $text -match '(^|[\\/])\.\.?([\\/]|$)' -or $text -match '^[\\/]*$') {
            throw "OwnedFolders entry '$folder' is not a sub-folder of the checkout (relative, no '.' or '..' segment): ownership would reach beyond this checkout."
        }
    }
    return @($folders | ForEach-Object { ([string]$_).Trim().Trim('\', '/').Replace('/', '\') })
}

function Get-YOwnPrefixes([string]$Root, [hashtable]$Config) {
    $full = [IO.Path]::GetFullPath($Root)
    return @(Get-YOwnedFolders $Config | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $full $_)).TrimEnd('\') + [IO.Path]::DirectorySeparatorChar })
}

# The owned prefix a path or command line falls under, or $null.
function Get-YMatchedPrefix([string]$Text, [string[]]$OwnPrefixes, [switch]$Contains) {
    if (-not $Text) { return $null }
    foreach ($prefix in $OwnPrefixes) {
        if ($Contains) { if ($Text.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $prefix } }
        elseif ($Text.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $prefix }
    }
    return $null
}

function Test-YOwnedProcess($Info, [string[]]$OwnPrefixes) {
    if (-not $Info) { return $false }
    $image = [string]$Info.ExecutablePath
    $commandLine = [string]$Info.CommandLine
    if ($commandLine -match $script:YToolchainPattern) { return $false }
    if (Get-YMatchedPrefix $image $OwnPrefixes) { return $true }
    # dotnet run / dotnet watch / the Blazor dev server: the launcher always passes the absolute
    # project path, so the command line carries an owned prefix of this checkout. A plain
    # `dotnet build` of the same project is not the application and is left alone.
    if ([string]$Info.Name -ieq 'dotnet.exe' -and (Get-YMatchedPrefix $commandLine $OwnPrefixes -Contains)) {
        return $commandLine -match '(?i)\s(run|watch)\s|\.dll\b'
    }
    return $false
}

function Get-YOwnedProcesses([string]$Root, [hashtable]$Config) {
    $prefixes = Get-YOwnPrefixes $Root $Config
    return @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessId -ne $PID -and (Test-YOwnedProcess $_ $prefixes) })
}

function Stop-YOwnedProcesses([string]$Root, [string]$Label, [hashtable]$Config = @{}) {
    $owned = Get-YOwnedProcesses $Root $Config
    if ($owned.Count -eq 0) { return }
    Write-YStep "Stopping the previous $Label instance of this checkout..."
    $prefixes = Get-YOwnPrefixes $Root $Config
    foreach ($p in $owned) {
        $imagePrefix = Get-YMatchedPrefix ([string]$p.ExecutablePath) $prefixes
        $why = if ($imagePrefix) { "image under $imagePrefix" } else { "command line under $(Get-YMatchedPrefix ([string]$p.CommandLine) $prefixes -Contains)" }
        Write-Host "  Stopping $($p.Name) (PID $($p.ProcessId)): $why" -ForegroundColor Yellow
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    # A desktop app keeps its WebView2 user-data folder and its assemblies locked until it has
    # really exited; relaunching before that fails the build or paints a white window.
    Wait-Process -Id @($owned.ProcessId) -Timeout 10 -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}

# A port is not an identity. A holder this checkout does not own is never stopped: when it is the
# same project from another checkout, this checkout takes its own ports; otherwise the launch fails
# and names the holder.
function Resolve-YPortCollision([hashtable]$Config, [string]$Root, [hashtable]$Runtime) {
    $prefixes = Get-YOwnPrefixes $Root $Config
    $foreign = @()
    foreach ($key in $Runtime.Ports.Keys) {
        $port = $Runtime.Ports[$key]
        $holders = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique)
        foreach ($holderId in $holders) {
            if ($holderId -eq 0 -or $holderId -eq 4) { continue }
            $info = Get-CimInstance Win32_Process -Filter "ProcessId=$holderId" -ErrorAction SilentlyContinue
            if (Test-YOwnedProcess $info $prefixes) { continue }
            $foreign += [pscustomobject]@{ Key = $key; Port = $port; Id = $holderId; Name = [string]$info.Name; CommandLine = [string]$info.CommandLine; Image = [string]$info.ExecutablePath }
        }
    }
    if ($foreign.Count -eq 0) { return $Runtime }

    foreach ($f in $foreign) {
        Write-Host "  Port $($f.Port) ($($f.Key)) is held by $($f.Name) (PID $($f.Id)), which this checkout does not own." -ForegroundColor Yellow
        if ($f.Image) { Write-Host "    $($f.Image)" -ForegroundColor DarkGray }
    }
    $nameProof = [regex]::Escape([string]$Config.Name)
    $sameProject = @($foreign | Where-Object { ($_.CommandLine + ' ' + $_.Image) -match "(?i)\\$nameProof[\\.]" }).Count -eq $foreign.Count
    if (-not $sameProject) {
        throw "Refusing to stop a process this checkout does not own. Free the port yourself, or give this checkout its own ports with -w."
    }
    if (Test-YInteractive) {
        Write-Host "  This is another checkout of $($Config.Name). Nothing will be stopped." -ForegroundColor Cyan
        $answer = Read-Host "  Give this checkout its own ports and write .ylaunch.local? [Y/n]"
        if ($answer -and $answer.Trim().ToLowerInvariant() -notin @('y', 'yes', 'o', 'oui')) {
            throw "Aborted. Nothing was stopped."
        }
    } else {
        Write-Host "  Non-interactive run: allocating free ports for this checkout." -ForegroundColor Cyan
    }
    New-YWorktreePorts -Config $Config -Root $Root -LocalPath $Runtime.LocalPath -Reason 'port collision'
    return (Resolve-YRuntime -Config $Config -Root $Root -Options @{})
}

# ============================================================
#  dotnet invocation (with the MAUI workload self-repair)
# ============================================================

# Returns nothing: the verdict is $script:YDotnetSucceeded. dotnet writes to the output stream, so a
# boolean return value would be buried behind its log lines and read as truthy.
$script:YDotnetSucceeded = $false
function Invoke-YDotnet([string[]]$Arguments, [string]$Root, [switch]$WorkloadRepair) {
    Push-Location -LiteralPath $Root
    # The errors go to a side file logger: piping dotnet (to read its output) would switch off the
    # MSBuild terminal logger on every build.
    $errorLog = Join-Path ([IO.Path]::GetTempPath()) "ylaunch-build-errors-$PID.log"
    try {
        $firstAttempt = if ($WorkloadRepair) { @($Arguments) + "-flp:ErrorsOnly;LogFile=$errorLog" } else { $Arguments }
        & dotnet @firstAttempt
        if ($LASTEXITCODE -eq 0) { $script:YDotnetSucceeded = $true; return }
        # Repair ONLY a build that failed because a workload is missing (NETSDK1147). 'dotnet workload
        # restore' needs an elevated prompt when the SDK lives under Program Files: running it on any
        # other failure (a compile error, a file locked by another build) raised a UAC prompt for
        # nothing, and 'dotnet workload list' is no proof either (MAUI on Windows builds from its
        # NuGet packages without the 'maui' workload being listed).
        $missingWorkload = $WorkloadRepair -and (Test-Path -LiteralPath $errorLog) -and
            (Select-String -LiteralPath $errorLog -Pattern 'NETSDK1147' -Quiet)
        if (-not $missingWorkload) { $script:YDotnetSucceeded = $false; return }
        Write-Host "  Missing .NET workloads (NETSDK1147): running 'dotnet workload restore'..." -ForegroundColor Yellow
        & dotnet workload restore | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  'dotnet workload restore' FAILED (an elevated prompt may be required)." -ForegroundColor Red
            $script:YDotnetSucceeded = $false
            return
        }
        Write-Host "  Workloads installed. The first attempt FAILED and its verdict is discarded; retrying..." -ForegroundColor Cyan
        & dotnet @Arguments
        $script:YDotnetSucceeded = ($LASTEXITCODE -eq 0)
    } finally {
        Remove-Item -LiteralPath $errorLog -Force -ErrorAction SilentlyContinue
        Pop-Location
    }
}

# ============================================================
#  Database: start, snapshot, reset
# ============================================================

function Get-YSnapshotDir([string]$Root) { return Join-Path $Root 'TestResults\DbSnapshots' }

function Invoke-YSnapshotRetention([string]$Dir) {
    Get-ChildItem -LiteralPath $Dir -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -Skip 20 |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

function Start-YDatabase([hashtable]$Db, [string]$Root) {
    if (-not $Db -or $Db.Kind -ne 'PostgresDocker') { return }
    Write-YStep "Ensuring the local PostgreSQL ($($Db.Container)) is running..."
    $running = & docker ps --filter "name=^/$($Db.Container)$" --format "{{.Names}}" 2>$null
    if ($running -ne $Db.Container) {
        & docker compose -f (Resolve-YPath $Root $Db.Compose) up -d | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "docker compose up failed for $($Db.Container). Is Docker Desktop running?" }
    }
    for ($i = 0; $i -lt 120; $i++) {
        $health = & docker inspect --format '{{.State.Health.Status}}' $Db.Container 2>$null
        if ($health -eq 'healthy') { Write-Host "  Database healthy." -ForegroundColor Green; return }
        Start-Sleep -Milliseconds 500
    }
    & docker logs --tail 40 $Db.Container 2>$null | Out-Host
    throw "Database $($Db.Container) not healthy after 60s."
}

# A reset is always preceded by a snapshot, and a failed snapshot aborts the reset.
function Save-YDatabaseSnapshot([hashtable]$Db, [string]$Root) {
    $dir = Get-YSnapshotDir $Root
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    if ($Db.Kind -eq 'PostgresDocker') {
        $running = & docker ps --filter "name=^/$($Db.Container)$" --format "{{.Names}}" 2>$null
        if ($running -ne $Db.Container) { Write-Host "  No running database to snapshot." -ForegroundColor DarkGray; return }
        $snapshot = Join-Path $dir "$($Db.Database)-$stamp.sql"
        $inner = "/tmp/$($Db.Database)-$stamp.sql"
        & docker exec $Db.Container sh -c "pg_dump -U $($Db.User) -d $($Db.Database) --no-owner --no-acl > $inner" 2>$null
        if ($LASTEXITCODE -ne 0) { & docker exec $Db.Container rm -f $inner 2>$null; throw "pg_dump failed: reset aborted to protect the data." }
        & docker cp "$($Db.Container):$inner" $snapshot 2>$null
        & docker exec $Db.Container rm -f $inner 2>$null
        if (-not (Test-Path -LiteralPath $snapshot) -or (Get-Item -LiteralPath $snapshot).Length -eq 0) {
            throw "Snapshot missing or empty: reset aborted to protect the data."
        }
        Write-Host "  Snapshot saved: $snapshot" -ForegroundColor Green
    } elseif ($Db.Kind -eq 'Sqlite') {
        $files = @(Get-YSqliteFiles $Db $Root)
        if ($files.Count -eq 0) { Write-Host "  No database file to snapshot." -ForegroundColor DarkGray; return }
        $target = Join-Path $dir "sqlite-$stamp"
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        foreach ($f in $files) { Copy-Item -LiteralPath $f.FullName -Destination $target -Force }
        if (@(Get-ChildItem -LiteralPath $target).Count -ne $files.Count) { throw "Snapshot incomplete: reset aborted to protect the data." }
        Write-Host "  Snapshot saved: $target" -ForegroundColor Green
    }
    Invoke-YSnapshotRetention $dir
}

function Get-YSqliteFiles([hashtable]$Db, [string]$Root) {
    $directory = [Environment]::ExpandEnvironmentVariables([string]$Db.Directory)
    $directory = Resolve-YPath $Root $directory
    if (-not (Test-Path -LiteralPath $directory)) { return @() }
    return @(Get-ChildItem -LiteralPath $directory -File -Filter $Db.FilePattern -ErrorAction SilentlyContinue)
}

function Reset-YDatabase([hashtable]$Db, [string]$Root) {
    if (-not $Db -or $Db.Kind -eq 'None') { Write-Host "  No database configured: -r has nothing to reset." -ForegroundColor DarkGray; return }
    Write-YStep "Resetting the local database (snapshot first)..."
    if ($Db.Kind -eq 'PostgresDocker') {
        Start-YDatabase $Db $Root
        Save-YDatabaseSnapshot $Db $Root
        & docker compose -f (Resolve-YPath $Root $Db.Compose) down -v | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "docker compose down -v failed." }
        Start-YDatabase $Db $Root
    } elseif ($Db.Kind -eq 'Sqlite') {
        Save-YDatabaseSnapshot $Db $Root
        foreach ($f in Get-YSqliteFiles $Db $Root) {
            Remove-Item -LiteralPath $f.FullName -Force
            Write-Host "  Deleted $($f.FullName)" -ForegroundColor DarkGray
        }
    }
    Write-Host "  Database reset." -ForegroundColor Green
}

function Get-YDatabaseEnv([hashtable]$Db) {
    $envMap = @{}
    if ($Db -and $Db.EnvVar -and $Db.ConnectionString) { $envMap[$Db.EnvVar] = $Db.ConnectionString }
    return $envMap
}

# ============================================================
#  Tests: TRX is the authoritative record
# ============================================================

function Read-YTrxCounters([string]$TrxPath) {
    if (-not (Test-Path -LiteralPath $TrxPath)) { return $null }
    try {
        [xml]$doc = Get-Content -LiteralPath $TrxPath -Raw
        $c = $doc.TestRun.ResultSummary.Counters
        $total = [int]$c.total
        $passed = [int]$c.passed
        $failed = [int]$c.failed + [int]$c.error + [int]$c.timeout + [int]$c.aborted
        return @{ Total = $total; Passed = $passed; Failed = $failed; Skipped = [math]::Max(0, $total - $passed - $failed) }
    } catch {
        Write-Warning "Unreadable TRX '$TrxPath': $($_.Exception.Message)"
        return $null
    }
}

function Invoke-YTestRun {
    param(
        [string]$Root,
        [string]$Target,
        [string]$Label,
        [string]$Runner,
        [string]$ResultsDir,
        [string[]]$ExtraArgs = @(),
        [hashtable]$Env = @{}
    )
    Write-YStep $Label
    New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
    $safe = ($Label -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
    $trxName = "{0}-{1:yyyyMMdd-HHmmssfff}.trx" -f $safe, (Get-Date)
    if ($Runner -eq 'MTP') {
        $switch = if ($Target -match '\.slnx?$') { '--solution' } else { '--project' }
        $arguments = @('test', $switch, $Target, '--no-build', '--configuration', 'Debug', '--no-progress',
                       '--report-trx', '--report-trx-filename', $trxName, '--results-directory', $ResultsDir)
    } else {
        $arguments = @('test', $Target, '--no-build', '--configuration', 'Debug', '--nologo',
                       '--logger', "trx;LogFileName=$trxName", '--results-directory', $ResultsDir)
    }
    $arguments += $ExtraArgs

    $saved = @{}
    foreach ($k in $Env.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, [string]$Env[$k]) }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $logPath = Join-Path $ResultsDir ($trxName -replace '\.trx$', '.log')
    try {
        Push-Location -LiteralPath $Root
        try { & dotnet @arguments 2>&1 | Tee-Object -FilePath $logPath | Out-Host; $exitCode = $LASTEXITCODE } finally { Pop-Location }
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
    $sw.Stop()

    $trxPath = Join-Path $ResultsDir $trxName
    $counters = Read-YTrxCounters $trxPath
    $result = @{ Label = $Label; ExitCode = $exitCode; Total = 0; Passed = 0; Failed = 0; Skipped = 0; Problem = $null }
    if (-not $counters) {
        $result.Problem = "no TRX produced ($trxPath): the suite cannot be proven to have run"
    } else {
        $result.Total = $counters.Total; $result.Passed = $counters.Passed; $result.Failed = $counters.Failed; $result.Skipped = $counters.Skipped
        if ($counters.Total -le 0) { $result.Problem = 'zero tests executed' }
        elseif ($exitCode -ne 0 -and $counters.Failed -eq 0) { $result.Problem = "dotnet test exited $exitCode with no failed test (log: $logPath)" }
    }
    $color = if ($result.Problem -or $result.Failed -gt 0) { 'Red' } else { 'Green' }
    Write-Host ("  {0}: {1} passed, {2} failed, {3} skipped, total {4} ({5:N1}s)" -f $Label, $result.Passed, $result.Failed, $result.Skipped, $result.Total, $sw.Elapsed.TotalSeconds) -ForegroundColor $color
    if ($result.Problem) { Write-Host "  $($result.Problem)" -ForegroundColor Red }
    if ($counters) { Write-Host "  TRX: $trxPath" -ForegroundColor DarkGray }
    return $result
}

function Get-YCoverageArgs([string]$Runner, [hashtable]$Suite, [string]$CoverageDir) {
    if ($Suite.CoverageArgs) { return @($Suite.CoverageArgs) }
    if ($Runner -eq 'MTP') { return @('--coverlet') }
    return @('--collect', 'XPlat Code Coverage')
}

function Invoke-YCoverageReport([hashtable]$Config, [string]$Root, [string]$CoverageDir) {
    Write-YStep "Generating the HTML coverage report..."
    $reports = @(Get-ChildItem -LiteralPath $CoverageDir -Recurse -File -Filter '*.cobertura*.xml' -ErrorAction SilentlyContinue)
    if ($reports.Count -eq 0) { throw "No Cobertura report found under ${CoverageDir}: coverage was not collected." }
    $reportDir = Join-Path $Root 'TestResults\CoverageReport'
    if (Test-Path -LiteralPath $reportDir) { Remove-Item -LiteralPath $reportDir -Recurse -Force }
    $reportArgs = @("-reports:$(($reports.FullName) -join ';')", "-targetdir:$reportDir", '-reporttypes:Html;Cobertura', '-verbosity:Warning')
    if ($Config.Coverage -and $Config.Coverage.AssemblyFilters) { $reportArgs += "-assemblyfilters:$($Config.Coverage.AssemblyFilters)" }
    $manifest = Join-Path $Root '.config\dotnet-tools.json'
    Push-Location -LiteralPath $Root
    try {
        if ((Test-Path -LiteralPath $manifest) -and ((Get-Content -LiteralPath $manifest -Raw) -match 'reportgenerator')) {
            & dotnet tool restore | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed." }
            & dotnet tool run reportgenerator @reportArgs
        } elseif (Get-Command reportgenerator -ErrorAction SilentlyContinue) {
            & reportgenerator @reportArgs
        } else {
            throw "reportgenerator not found: pin dotnet-reportgenerator-globaltool in .config/dotnet-tools.json."
        }
        if ($LASTEXITCODE -ne 0) { throw "ReportGenerator failed." }
    } finally {
        Pop-Location
    }
    $merged = Join-Path $reportDir 'Cobertura.xml'
    if (-not (Test-Path -LiteralPath $merged)) { throw "Merged Cobertura report was not produced." }
    [xml]$xml = Get-Content -LiteralPath $merged -Raw
    $covered = [long]$xml.coverage.'lines-covered'
    $valid = [long]$xml.coverage.'lines-valid'
    if ($valid -le 0) { throw "Merged Cobertura report has no measurable line." }
    $rate = 100.0 * $covered / $valid
    Write-Host ("  Line coverage: {0:N2}% ({1:N0}/{2:N0})" -f $rate, $covered, $valid) -ForegroundColor Cyan
    if ($Config.Coverage -and $Config.Coverage.MinLine -and $rate -lt [double]$Config.Coverage.MinLine) {
        throw ("Line coverage {0:N2}% is below the required {1}%." -f $rate, $Config.Coverage.MinLine)
    }
    $index = Join-Path $reportDir 'index.html'
    Write-Host "  Coverage report: $index" -ForegroundColor Green
    if (Test-YInteractive) { Start-Process $index }
}

function Resolve-YE2eCategories([string[]]$Categories, [string]$Filter) {
    if (-not $Filter -or $Filter -in @('all', '*')) { return @() }
    $resolved = New-Object System.Collections.Generic.List[string]
    foreach ($part in ($Filter -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        if ($part -match '^\d+$') {
            $i = [int]$part - 1
            if ($i -lt 0 -or $i -ge $Categories.Count) { throw "Unknown E2E category number: $part (valid: 1-$($Categories.Count))" }
            $resolved.Add($Categories[$i])
        } elseif ($Categories -icontains $part) {
            $resolved.Add(($Categories | Where-Object { $_ -ieq $part } | Select-Object -First 1))
        } else {
            throw "Unknown E2E category '$part'. Valid: $($Categories -join ', ')"
        }
    }
    return $resolved.ToArray()
}

function Show-YE2eMenu([string[]]$Categories) {
    Write-Host ""
    Write-Host "  E2E categories" -ForegroundColor Cyan
    for ($i = 0; $i -lt $Categories.Count; $i++) { Write-Host "  $($i + 1). $($Categories[$i])" }
    $answer = (Read-Host "  Numbers or names (comma-separated), Enter or * for all").Trim()
    return Resolve-YE2eCategories $Categories $answer
}

function Write-YCombinedSummary([object[]]$Results) {
    $passed = ($Results | ForEach-Object { $_.Passed } | Measure-Object -Sum).Sum
    $failed = ($Results | ForEach-Object { $_.Failed } | Measure-Object -Sum).Sum
    $skipped = ($Results | ForEach-Object { $_.Skipped } | Measure-Object -Sum).Sum
    $total = ($Results | ForEach-Object { $_.Total } | Measure-Object -Sum).Sum
    $broken = @($Results | Where-Object { $_.Problem })
    Write-Host ""
    Write-Host "  =========================================" -ForegroundColor Cyan
    Write-Host "  Combined ($(($Results | ForEach-Object { $_.Label }) -join ' + '))" -ForegroundColor White
    Write-Host "  Passed : $passed" -ForegroundColor Green
    Write-Host "  Failed : $failed" -ForegroundColor $(if ($failed -gt 0) { 'Red' } else { 'DarkGray' })
    if ($skipped -gt 0) { Write-Host "  Skipped: $skipped" -ForegroundColor Yellow }
    if ($broken.Count -gt 0) { Write-Host "  Unproven suites: $(($broken | ForEach-Object { $_.Label }) -join ', ')" -ForegroundColor Red }
    Write-Host "  Total  : $total" -ForegroundColor White
    Write-Host "  =========================================" -ForegroundColor Cyan
    if ($failed -gt 0 -or $broken.Count -gt 0) { Write-YFail "  SOME TESTS FAILED."; return }
    Write-Host "  All tests passed." -ForegroundColor Green
}

# ============================================================
#  Web host: components as jobs, readiness, grouped shutdown
# ============================================================

function Write-YFrontSettings([hashtable]$Config, [string]$Root, [hashtable]$Runtime) {
    $web = $Config.Web
    if (-not $web.FrontSettingsFile) { return }
    $settings = [ordered]@{}
    foreach ($key in (Get-YKeys $web.FrontSettings)) { $settings[$key] = Expand-YValue ([string]$web.FrontSettings[$key]) $Config $Runtime }
    $branch = (& git -C $Root rev-parse --abbrev-ref HEAD 2>$null)
    $settings['DevBanner'] = [ordered]@{ Branch = "$branch".Trim(); Label = $Runtime.Label }
    $path = Resolve-YPath $Root $web.FrontSettingsFile
    ($settings | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $path -Encoding utf8
}

function Start-YWebComponents([hashtable]$Config, [string]$Root, [hashtable]$Runtime, [bool]$HotReload, [hashtable]$ExtraEnv) {
    $jobs = [ordered]@{}
    $dbEnv = Get-YDatabaseEnv $Config.Database
    foreach ($component in @($Config.Web.Components)) {
        $project = Resolve-YPath $Root $component.Project
        $workDir = Resolve-YWorkingDirectory -Root $Root -Project $project -Override $component.WorkingDirectory -Label $component.Key
        $urls = @(0..(@($component.Urls).Count - 1) | ForEach-Object { Get-YComponentUrl $component $Runtime $_ })
        $envMap = @{ ASPNETCORE_ENVIRONMENT = 'Development'; ASPNETCORE_URLS = ($urls -join ';'); DOTNET_DISABLE_GUI_ERRORS = '1' }
        if ($component.UsesDatabase) { foreach ($k in $dbEnv.Keys) { $envMap[$k] = $dbEnv[$k] } }
        foreach ($k in (Get-YKeys $component.Env)) { $envMap[$k] = Expand-YValue ([string]$component.Env[$k]) $Config $Runtime }
        foreach ($k in (Get-YKeys $ExtraEnv)) { $envMap[$k] = Expand-YValue ([string]$ExtraEnv[$k]) $Config $Runtime }
        $mode = if ($HotReload) { 'hot reload' } else { 'no build' }
        Write-YStep "Starting $($component.Key) ($($urls -join ', '), $mode)..."
        # The absolute project path is on the command line on purpose: it is what proves ownership
        # when the next launch stops this checkout's previous instance.
        # The working directory is the content root of an ASP.NET host started with
        # --no-launch-profile, and the base of every relative path it reads (appsettings, SQLite file,
        # keys): the project folder unless configured, never wherever the launcher was called from.
        # `dotnet run` starts the app in the project's RunWorkingDirectory (the Web SDK sets it to the
        # project folder, other SDKs leave it empty, i.e. the caller's folder), so the launcher both
        # moves the job there and passes the property: the configured folder wins for every SDK.
        Write-Host "  Working directory: $workDir" -ForegroundColor DarkGray
        $jobs[$component.Key] = Start-Job -ScriptBlock {
            param($ProjectPath, $EnvMap, $Watch, $WorkDir)
            Set-Location -LiteralPath $WorkDir
            foreach ($entry in $EnvMap.GetEnumerator()) { Set-Item -Path "env:$($entry.Key)" -Value $entry.Value }
            if ($Watch) {
                $env:DOTNET_WATCH_RESTART_ON_RUDE_EDIT = '1'
                dotnet watch run --project $ProjectPath --no-launch-profile "--property:RunWorkingDirectory=$WorkDir" 2>&1
            } else {
                dotnet run --project $ProjectPath --no-build --configuration Debug --no-launch-profile "--property:RunWorkingDirectory=$WorkDir" 2>&1
            }
            "##YEXIT##$LASTEXITCODE"
        } -ArgumentList $project, $envMap, $HotReload, $workDir
        $script:YActiveJobs = $jobs
    }
    return $jobs
}

# A component's working directory: its WorkingDirectory setting (relative to the checkout root) when
# given, else the folder of its project file. A missing folder fails the launch instead of silently
# running somewhere else.
function Resolve-YWorkingDirectory([string]$Root, [string]$Project, [string]$Override, [string]$Label) {
    $dir = if ($Override) { Resolve-YPath $Root $Override } else { Split-Path $Project -Parent }
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { throw "Working directory of $Label not found: $dir" }
    # No trailing separator: it would escape the closing quote of the --property argument.
    return $dir.TrimEnd('\', '/')
}

function Wait-YEndpoint([string]$Url, [string]$Label, [int]$MaxSeconds, [hashtable]$Jobs) {
    Write-Host "  Waiting for $Label ($Url)..." -ForegroundColor DarkGray
    for ($waited = 0; $waited -lt $MaxSeconds; $waited++) {
        try {
            $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2 -SkipCertificateCheck -ErrorAction Stop
            if ($r.StatusCode -lt 400) { Write-Host "  $Label ready after ${waited}s." -ForegroundColor Green; return $true }
        } catch { }
        if ($Jobs) {
            $dead = @($Jobs.Keys | Where-Object { $Jobs[$_].State -in @('Completed', 'Failed', 'Stopped') })
            if ($dead.Count -gt 0) { Write-Host "  $($dead -join ', ') stopped before $Label was ready." -ForegroundColor Red; return $false }
        }
        Start-Sleep -Seconds 1
    }
    Write-Host "  $Label did not respond after ${MaxSeconds}s." -ForegroundColor Red
    return $false
}

function Wait-YWebReady([hashtable]$Config, [hashtable]$Runtime, [hashtable]$Jobs) {
    foreach ($component in @($Config.Web.Components)) {
        $path = if ($component.Health) { $component.Health } else { '/' }
        $timeout = if ($Config.Web.ReadySeconds) { [int]$Config.Web.ReadySeconds } else { 90 }
        if (-not (Wait-YEndpoint ((Get-YComponentUrl $component $Runtime) + $path) $component.Key $timeout $Jobs)) { return $false }
    }
    return $true
}

# PIDs of every live descendant of a process. Taken as one snapshot, because a grandchild whose
# parent has just exited no longer points back to this launcher.
function Get-YDescendantIds([int]$RootId) {
    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Select-Object ProcessId, ParentProcessId)
    $found = New-Object System.Collections.Generic.HashSet[int]
    $queue = New-Object System.Collections.Generic.Queue[int]
    $queue.Enqueue($RootId)
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        foreach ($p in $all) {
            if ($p.ParentProcessId -eq $current -and $p.ProcessId -ne $RootId -and $found.Add([int]$p.ProcessId)) { $queue.Enqueue([int]$p.ProcessId) }
        }
    }
    # The comma keeps the set whole: PowerShell would otherwise unroll it, and an empty set into $null.
    return ,$found
}

# Stops only what THIS launcher started (its descendants that belong to the checkout). A checkout-wide
# sweep here would kill the new instance when a relaunch replaces this one.
function Stop-YWebComponents([hashtable]$Jobs, [hashtable]$Config, [string]$Root, [bool]$DumpLogs) {
    Write-YStep "Shutting down..."
    $prefixes = Get-YOwnPrefixes $Root $Config
    $mine = Get-YDescendantIds $PID
    $targets = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $mine.Contains([int]$_.ProcessId) -and (Test-YOwnedProcess $_ $prefixes) })
    # Application processes first, while their `dotnet run` jobs are still alive: disposing the jobs
    # first orphans the grandchildren (the Blazor dev server, the apphosts). A desktop app started
    # alongside the web host is a direct child of this launcher, so it is stopped here too.
    foreach ($p in $targets) {
        Write-Host "  Stopping $($p.Name) (PID $($p.ProcessId))" -ForegroundColor Yellow
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    foreach ($key in @($Jobs.Keys)) {
        Wait-Job -Job $Jobs[$key] -Timeout 3 -ErrorAction SilentlyContinue | Out-Null
        if ($Jobs[$key].State -notin @('Completed', 'Failed', 'Stopped')) { Stop-Job -Job $Jobs[$key] -ErrorAction SilentlyContinue }
        if ($DumpLogs) {
            Write-Host "  Last output of $key`:" -ForegroundColor Yellow
            @(Receive-Job -Job $Jobs[$key] -ErrorAction SilentlyContinue) | Select-Object -Last 60 |
                ForEach-Object { Write-Host "  [$key] $_" -ForegroundColor DarkYellow }
        }
        Remove-Job -Job $Jobs[$key] -Force -ErrorAction SilentlyContinue
    }
    $script:YActiveJobs = $null
    foreach ($id in $mine) {
        $left = Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue
        if ($left -and (Test-YOwnedProcess $left $prefixes)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    }
    Write-Host "  Done." -ForegroundColor Green
}

# Streams every component's output; the components live and die together. $Desktop is the desktop
# app started alongside the web host (a process): closing its window stops every component.
function Watch-YWebComponents([hashtable]$Jobs, [hashtable]$Config, [string]$Root, $Desktop = $null) {
    $colors = @('DarkCyan', 'DarkGreen', 'DarkYellow', 'DarkMagenta')
    $exitCodes = @{}
    Write-Host ""
    Write-Host "Press Ctrl+C to stop every component." -ForegroundColor Magenta
    try {
        while ($true) {
            if ($Desktop -and $Desktop.HasExited) {
                $code = $Desktop.ExitCode
                Write-Host "$($Config.Name) desktop app exited (code $code): stopping every component." -ForegroundColor $(if ($code -eq 0) { 'Yellow' } else { 'Red' })
                if ($code -ne 0) { $script:YLaunchExitCode = 1 }
                break
            }
            $i = 0
            foreach ($key in @($Jobs.Keys)) {
                $color = $colors[$i % $colors.Count]; $i++
                foreach ($line in @(Receive-Job -Job $Jobs[$key] -ErrorAction SilentlyContinue)) {
                    if ("$line" -match '^##YEXIT##(-?\d+)$') { $exitCodes[$key] = $Matches[1] } else { Write-Host ("[{0,-6}] {1}" -f $key.ToUpperInvariant(), $line) -ForegroundColor $color }
                }
            }
            $down = @($Jobs.Keys | Where-Object { $Jobs[$_].State -in @('Completed', 'Failed', 'Stopped') }) | Select-Object -First 1
            if ($down) {
                foreach ($line in @(Receive-Job -Job $Jobs[$down] -ErrorAction SilentlyContinue)) {
                    if ("$line" -match '^##YEXIT##(-?\d+)$') { $exitCodes[$down] = $Matches[1] }
                }
                $code = if ($exitCodes.ContainsKey($down)) { $exitCodes[$down] } else { 'unknown' }
                Write-Host "$down stopped (state $($Jobs[$down].State), exit code $code): stopping every component." -ForegroundColor Red
                $script:YLaunchExitCode = 1
                break
            }
            Start-Sleep -Milliseconds 500
        }
    } finally {
        Stop-YWebComponents -Jobs $Jobs -Config $Config -Root $Root -DumpLogs $false
    }
}

# ============================================================
#  Desktop (MAUI) host
# ============================================================

function Find-YMauiExe([hashtable]$Maui, [string]$Root) {
    $projectDir = Split-Path (Resolve-YPath $Root $Maui.Project) -Parent
    $binDir = Join-Path $projectDir "bin\Debug\$($Maui.Tfm)"
    if (-not (Test-Path -LiteralPath $binDir)) { return $null }
    return Get-ChildItem -LiteralPath $binDir -Recurse -File -Filter "$($Maui.ExeName).exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\publish\\' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

# -Companion: the desktop app runs alongside web components started by this launcher. It never
# detaches (the web components live in this launcher) and returns a handle for the watch loop instead
# of blocking: @{ Process = ... } for the built executable, @{ Job = ... } under hot reload.
function Start-YMauiApp([hashtable]$Config, [string]$Root, [hashtable]$Runtime, [bool]$HotReload, [int]$DebugPort, [bool]$Silent, [switch]$Companion) {
    $maui = $Config.Maui
    $project = Resolve-YPath $Root $maui.Project
    $envMap = Get-YDatabaseEnv $Config.Database
    foreach ($k in (Get-YKeys $maui.Env)) { $envMap[$k] = Expand-YValue ([string]$maui.Env[$k]) $Config $Runtime }
    # WebView2 reads its extra switches from this variable when it is created, so it must be in the
    # app's environment before start, not on its command line.
    if ($DebugPort -gt 0) { $envMap['WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'] = "--remote-debugging-port=$DebugPort" }

    if ($HotReload -and $Companion) {
        $workDir = Resolve-YWorkingDirectory -Root $Root -Project $project -Override $maui.WorkingDirectory -Label 'the desktop app'
        Write-YStep "Launching $($Config.Name) desktop app with hot reload (dotnet watch, $workDir)..."
        $job = Start-Job -ScriptBlock {
            param($ProjectPath, $Tfm, $EnvMap, $WorkDir)
            Set-Location -LiteralPath $WorkDir
            foreach ($entry in $EnvMap.GetEnumerator()) { Set-Item -Path "env:$($entry.Key)" -Value $entry.Value }
            dotnet watch run --project $ProjectPath -f $Tfm "--property:RunWorkingDirectory=$WorkDir" 2>&1
            "##YEXIT##$LASTEXITCODE"
        } -ArgumentList $project, $maui.Tfm, $envMap, $workDir
        return @{ Job = $job }
    }

    $saved = @{}
    foreach ($k in $envMap.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, [string]$envMap[$k]) }
    try {
        if ($HotReload) {
            $workDir = Resolve-YWorkingDirectory -Root $Root -Project $project -Override $maui.WorkingDirectory -Label 'the desktop app'
            Write-YStep "Launching $($Config.Name) with hot reload (dotnet watch, $workDir)..."
            Push-Location -LiteralPath $workDir
            try { & dotnet watch run --project $project -f $maui.Tfm "--property:RunWorkingDirectory=$workDir" } finally { Pop-Location }
            return
        }
        $exe = Find-YMauiExe $maui $Root
        if (-not $exe) { throw "Executable $($maui.ExeName).exe not found under the Debug output of $project." }
        # The built executable runs from its output folder, as it does once installed, unless the
        # configuration names another folder.
        $workDir = if ($maui.WorkingDirectory) { Resolve-YWorkingDirectory -Root $Root -Project $project -Override $maui.WorkingDirectory -Label 'the desktop app' } else { $exe.DirectoryName }
        Write-YStep "Launching $($Config.Name) ($($exe.FullName))..."
        $proc = Start-Process -FilePath $exe.FullName -WorkingDirectory $workDir -PassThru
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }

    $readySeconds = if ($maui.ReadySeconds) { [int]$maui.ReadySeconds } else { 60 }
    $ready = $false
    for ($i = 0; $i -lt $readySeconds * 2; $i++) {
        if ($proc.HasExited) { throw "$($Config.Name) exited during startup (exit code $($proc.ExitCode))." }
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        throw "$($Config.Name) showed no window after ${readySeconds}s; stopped."
    }
    Write-Host "  Window up (PID $($proc.Id))." -ForegroundColor Green
    if ($DebugPort -gt 0) {
        if (-not (Wait-YEndpoint "http://127.0.0.1:$DebugPort/json/version" 'WebView2 CDP' 30 $null)) {
            throw "The WebView2 CDP endpoint did not answer on port $DebugPort."
        }
        Write-Host "  CDP: http://127.0.0.1:$DebugPort/json/list" -ForegroundColor Green
    }
    if ($Companion) { return @{ Process = $proc } }
    if ($Silent) {
        Write-Host "  Running detached (PID $($proc.Id)); the next launch stops it." -ForegroundColor DarkGray
        return
    }
    Write-Host "  Press Ctrl+C to stop the application." -ForegroundColor Magenta
    try {
        while (-not $proc.HasExited) { Start-Sleep -Milliseconds 500 }
        Write-Host "  Application exited (code $($proc.ExitCode))." -ForegroundColor DarkGray
    } finally {
        if (-not $proc.HasExited) {
            Write-Host "  Stopping the application..." -ForegroundColor Yellow
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

# ============================================================
#  Main flow
# ============================================================

function Invoke-YLaunchCore([hashtable]$Config, [string]$Root, [hashtable]$Options) {
    $Root = [IO.Path]::GetFullPath($Root)
    $hasWeb = $Config.ContainsKey('Web')
    $hasMaui = $Config.ContainsKey('Maui')
    $suites = @($Config.Tests | Where-Object { $_ })

    # ---------- implications ----------
    $e2eFilter = [string](Get-YOption $Options 'TestE2eFilter')
    $runE2e = [bool](Get-YOption $Options 'TestE2e') -or [bool]$e2eFilter
    $runAll = [bool](Get-YOption $Options 'TestAll')
    $runUnit = [bool](Get-YOption $Options 'TestUnit') -or $runAll
    $coverage = [bool](Get-YOption $Options 'Coverage')
    # A capability the project does not have is either declared ($LaunchConfig.Exceptions, printed on
    # every run) or an error: an absence is never skipped silently.
    $e2eException = Get-YDeclaredException -Config $Config -Key 'E2E'
    Test-YSeedAccounts $Config
    if ($runAll) {
        if ($Config.ContainsKey('E2E')) { $runE2e = $true }
        elseif ($e2eException) { Write-Host "  Declared exception: no E2E suite ($e2eException). -ta runs the other suites." -ForegroundColor Yellow }
        else { throw "This project has no E2E suite. If that is intended, declare it: `$LaunchConfig.Exceptions = @{ E2E = '<reason>' }." }
    }
    if ($runE2e -and -not $Config.ContainsKey('E2E')) {
        $why = if ($e2eException) { " (declared exception: $e2eException)" } else { '' }
        throw "This project has no E2E suite$why (-te/-tec)."
    }
    $selected = @($suites | Where-Object {
        ($runUnit -and $_.Kind -eq 'Unit') -or
        ($runAll -and $_.Kind -eq 'Integration') -or
        ($coverage -and $_.Kind -eq 'Unit' -and $_.Coverage) -or
        [bool](Get-YOption $Options $_.Flag)
    })
    if ($runAll -and $selected.Count -eq 0 -and -not $runE2e) { throw "-ta found no suite to run: declare Tests or E2E in `$LaunchConfig." }
    $anyTest = $selected.Count -gt 0 -or $runE2e
    $hotReload = [bool](Get-YOption $Options 'HotReload')
    $silent = [bool](Get-YOption $Options 'Silent')
    $debugPort = [int](Get-YOption $Options 'DebugPort')

    # ---------- runtime settings + preflight ----------
    $runtime = Resolve-YRuntime -Config $Config -Root $Root -Options $Options
    Show-YVersionGuard -Root $Root -Solution (Resolve-YPath $Root $Config.Solution)
    Show-YRulesPreflight -Root $Root

    # ---------- 1. stop the previous instance (always first) ----------
    Stop-YOwnedProcesses -Root $Root -Label $Config.Name -Config $Config
    # Ports matter only to a run that starts the web components: a unit-test run starts none, so a
    # port held by anything (another checkout, another project) is none of its business.
    $startsWeb = $hasWeb -and (-not $anyTest -or $runE2e)
    if ($startsWeb) { $runtime = Resolve-YPortCollision -Config $Config -Root $Root -Runtime $runtime }
    if ($hasWeb) { Write-YFrontSettings -Config $Config -Root $Root -Runtime $runtime }

    # ---------- 2. build ----------
    if (-not $hotReload -or $anyTest) {
        Write-YStep "Building $($Config.Solution)..."
        Invoke-YDotnet -Arguments @('build', (Resolve-YPath $Root $Config.Solution), '--configuration', 'Debug', '-nologo') -Root $Root -WorkloadRepair:$hasMaui
        if (-not $script:YDotnetSucceeded) { Write-YFail "BUILD FAILED."; return }
        if ($hasMaui) {
            Invoke-YDotnet -Arguments @('build', (Resolve-YPath $Root $Config.Maui.Project), '-f', $Config.Maui.Tfm, '--configuration', 'Debug', '-nologo') -Root $Root -WorkloadRepair
            if (-not $script:YDotnetSucceeded) { Write-YFail "DESKTOP BUILD FAILED."; return }
        }
        Write-Host "  Build succeeded." -ForegroundColor Green
    }

    # ---------- 3. tests (exit after) ----------
    if ($anyTest) {
        $runner = Get-YTestRunner $Root
        $results = @()
        $coverageDir = Join-Path $Root 'TestResults\Coverage'
        if ($coverage -and (Test-Path -LiteralPath $coverageDir)) { Remove-Item -LiteralPath $coverageDir -Recurse -Force }
        foreach ($suite in $selected) {
            $withCoverage = $coverage -and $suite.Coverage
            $resultsDir = if ($withCoverage) { $coverageDir } else { Join-Path $Root 'TestResults\Launcher' }
            $extra = if ($withCoverage) { Get-YCoverageArgs $runner $suite $coverageDir } else { @() }
            $r = Invoke-YTestRun -Root $Root -Target (Resolve-YPath $Root $suite.Project) -Label "$($suite.Key) tests" -Runner $runner -ResultsDir $resultsDir -ExtraArgs $extra
            $results += $r
            if ($r.Failed -gt 0 -or $r.Problem) { Write-Host "  $($suite.Key) suite failed: stopping the test run." -ForegroundColor Red; break }
        }
        $unitBroken = @($results | Where-Object { $_.Failed -gt 0 -or $_.Problem }).Count -gt 0
        if ($coverage -and -not $unitBroken) { Invoke-YCoverageReport -Config $Config -Root $Root -CoverageDir $coverageDir }

        if ($runE2e -and -not $unitBroken) {
            $e2e = $Config.E2E
            $categories = @($e2e.Categories)
            $picked = if ($e2eFilter -eq '?') { Show-YE2eMenu $categories } else { Resolve-YE2eCategories $categories $e2eFilter }
            $extra = @()
            $label = 'E2E tests'
            if ($picked.Count -gt 0) {
                $extra = @('--filter', (($picked | ForEach-Object { "Category=$_" }) -join '|'))
                $label = "E2E tests [$($picked -join ', ')]"
            }
            if ($e2e.PlaywrightScript) {
                $playwright = Resolve-YPath $Root $e2e.PlaywrightScript
                if (-not (Test-Path -LiteralPath $playwright)) { Write-YFail "playwright.ps1 not found at $playwright."; return }
                & pwsh -NoProfile -File $playwright install
                if ($LASTEXITCODE -ne 0) { Write-YFail "Playwright browser install failed."; return }
            }
            if ($e2e.Database) { Start-YDatabase $e2e.Database $Root } else { Start-YDatabase $Config.Database $Root }
            $e2eEnv = @{}
            foreach ($k in (Get-YKeys $e2e.ServerEnv)) { $e2eEnv[$k] = $e2e.ServerEnv[$k] }
            if ($e2e.Database) { foreach ($kv in (Get-YDatabaseEnv $e2e.Database).GetEnumerator()) { $e2eEnv[$kv.Key] = $kv.Value } }
            $jobs = Start-YWebComponents -Config $Config -Root $Root -Runtime $runtime -HotReload $false -ExtraEnv $e2eEnv
            if (-not (Wait-YWebReady -Config $Config -Runtime $runtime -Jobs $jobs)) {
                Stop-YWebComponents -Jobs $jobs -Config $Config -Root $Root -DumpLogs $true
                Write-YFail "Servers not ready: E2E aborted."
                return
            }
            $testEnv = @{}
            foreach ($k in (Get-YKeys $e2e.TestEnv)) { $testEnv[$k] = Expand-YValue ([string]$e2e.TestEnv[$k]) $Config $runtime }
            try {
                $results += Invoke-YTestRun -Root $Root -Target (Resolve-YPath $Root $e2e.Project) -Label $label -Runner $runner -ResultsDir (Join-Path $Root 'TestResults\Launcher') -ExtraArgs $extra -Env $testEnv
            } finally {
                Stop-YWebComponents -Jobs $jobs -Config $Config -Root $Root -DumpLogs ($results[-1].Failed -gt 0)
            }
        }
        Write-YCombinedSummary $results
        return
    }

    # ---------- 4. database ----------
    if (Get-YOption $Options 'Reset') { Reset-YDatabase $Config.Database $Root } else { Start-YDatabase $Config.Database $Root }

    # ---------- 5. start ----------
    if ($hasMaui -and -not $hasWeb) {
        Show-YSeedAccounts $Config $Root
        Start-YMauiApp -Config $Config -Root $Root -Runtime $runtime -HotReload $hotReload -DebugPort $debugPort -Silent $silent
        return
    }
    if ($hasWeb) {
        # Web first: a desktop app started alongside reads the web host's URL ({URL:Key} in Maui.Env)
        # and must find it answering.
        $jobs = Start-YWebComponents -Config $Config -Root $Root -Runtime $runtime -HotReload $hotReload -ExtraEnv @{}
        if (-not (Wait-YWebReady -Config $Config -Runtime $runtime -Jobs $jobs)) {
            Stop-YWebComponents -Jobs $jobs -Config $Config -Root $Root -DumpLogs $true
            Write-YFail "Startup failed: every component stopped."
            return
        }
        $desktop = $null
        if ($hasMaui) {
            $handle = Start-YMauiApp -Config $Config -Root $Root -Runtime $runtime -HotReload $hotReload -DebugPort $debugPort -Silent $silent -Companion
            if ($handle.Job) { $jobs['Desktop'] = $handle.Job; $script:YActiveJobs = $jobs }
            $desktop = $handle.Process
        }
        $browserComponent = @($Config.Web.Components | Where-Object { $_.Browser }) | Select-Object -First 1
        if ($browserComponent -and -not $silent) {
            $url = Get-YComponentUrl $browserComponent $runtime
            Write-YStep "Opening the browser -> $url"
            Start-Process $url
        } elseif ($hasMaui) {
            Write-YStep "Ready (web and desktop hosts; the launcher stays attached because the web components live in it)"
        } else {
            Write-YStep "Ready (no browser with -s)"
        }
        Show-YSeedAccounts $Config $Root
        Watch-YWebComponents -Jobs $jobs -Config $Config -Root $Root -Desktop $desktop
    }
}

# Entry point called by the root ylaunch.ps1. Sets $script:YLaunchExitCode; never exits itself.
function Invoke-YLaunch([hashtable]$Config, [string]$Root, [hashtable]$Options) {
    $ErrorActionPreference = 'Stop'
    # Every build of the run starts its own MSBuild nodes and leaves none behind (1.0.6).
    $previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
    $env:MSBUILDDISABLENODEREUSE = '1'
    try {
        if (Get-YOption $Options 'Help') { Show-YHelp $Config; return }
        if (Get-YOption $Options 'HelpLong') { Show-YHelp $Config -Long; return }
        Invoke-YLaunchCore -Config $Config -Root $Root -Options $Options
    } catch {
        Write-YFail "ylaunch: $($_.Exception.Message)"
        if ($script:YActiveJobs) { Stop-YWebComponents -Jobs $script:YActiveJobs -Config $Config -Root $Root -DumpLogs $true }
    } finally {
        $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
    }
}
