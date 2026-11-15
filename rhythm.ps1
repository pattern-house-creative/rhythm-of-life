# rhythm.ps1: the ./rhythm commands for PowerShell, with nothing but Docker (cli/README.md):
#   .\rhythm.ps1 init | up | down | logs | backup | snapshots | restore <snapshot> | upgrade [<tag>] [--pull]
#   .\rhythm.ps1 <anything else>   runs inside the engine image: connect, disconnect, doctor, token, check-config, ...
# init records COMPOSE_PROJECT_NAME and RHYTHM_VERSION in deploy/compose/.env, so later commands act on this install's own
# project; they and the other .env names can still be overridden from the environment.
# No param block, so every argument, --flags included, passes through unchanged. Works in Windows PowerShell 5.1.
# (engine-cli, not cli: PowerShell already has an alias named cli.)
# A failed docker call throws; ErrorActionPreference stays as it is, because Compose writes progress to stderr.
$deploy = Join-Path $PSScriptRoot 'deploy/compose'
$envFile = Join-Path $deploy '.env'

# Compose runs in deploy/compose, as deploy/backups/backup.sh does, so .env's COMPOSE_FILE applies too.
# rhythm.yaml's hash for the engine and app, set for the docker call only: Compose does not notice a changed config
# file on its own, so without it up would leave them running on the old settings.
function settingsHash {
    $line = Get-Content .env -ErrorAction SilentlyContinue | Where-Object { $_ -match '^RHYTHM_CONFIG_FILE=.' } | Select-Object -Last 1
    $file = if ($line) { $line.Substring('RHYTHM_CONFIG_FILE='.Length) } else { './rhythm.yaml' }
    if (Test-Path $file) { (Get-FileHash $file).Hash } else { '' }
}
# A project exported in the environment is named to Compose as well (-p), as Compose would take it anyway.
function compose { Push-Location $deploy; $saved = $env:RHYTHM_SETTINGS_HASH; try { $env:RHYTHM_SETTINGS_HASH = settingsHash; $named = @(); if ($env:COMPOSE_PROJECT_NAME) { $named = @('-p', $env:COMPOSE_PROJECT_NAME) }; docker compose @named @args } finally { $env:RHYTHM_SETTINGS_HASH = $saved; Pop-Location } }
function dc { compose @args; if ($LASTEXITCODE -ne 0) { throw "docker compose $($args -join ' ') failed ($LASTEXITCODE)" } }
function engine-cli { dc --profile cli run --rm cli @args }
function up { engine-cli preflight; dc up -d --wait @args; engine-cli ready }
function stamp { (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'") }
# A backup, then its staging folder's plain copy removed once restic has it and read it back (as deploy/backups/backup.sh).
function backup { $s = "/staging/$(stamp)"; dc --profile backup run --rm backup backup /var/lib/rhythm/archive $s; dc --profile backup run --rm --no-deps --entrypoint rm backup -r $s }
function version {
    if ($env:RHYTHM_VERSION) { return $env:RHYTHM_VERSION }
    $line = Get-Content $envFile -ErrorAction SilentlyContinue | Where-Object { $_ -match '^RHYTHM_VERSION=.' } | Select-Object -Last 1
    if ($line) { $line.Substring('RHYTHM_VERSION='.Length) } else { 'dev' }
}
# The Compose project a command acts on: the environment's, else the one init recorded in .env. Empty when neither names one.
function project {
    if ($env:COMPOSE_PROJECT_NAME) { return $env:COMPOSE_PROJECT_NAME }
    $line = Get-Content $envFile -ErrorAction SilentlyContinue | Where-Object { $_ -match '^COMPOSE_PROJECT_NAME=.' } | Select-Object -Last 1
    if ($line) { $line.Substring('COMPOSE_PROJECT_NAME='.Length) } else { '' }
}
function retag($from, $to) {
    foreach ($image in 'rhythm-engine', 'rhythm-app', 'rhythm-backup') {
        if (docker image ls -q "${image}:$from") { docker tag "${image}:$from" "${image}:$to" }
    }
}
# After an upgrade, a reminder that changes nothing: the pins this install builds from or runs that were last checked
# over 90 days ago (deploy/image-pins.txt), and images it runs that the list does not pin, such as an override in .env,
# which no upgrade moves.
function pins {
    $since = @{}; $used = @()
    foreach ($line in Get-Content (Join-Path $PSScriptRoot 'deploy/image-pins.txt')) {
        $f = -split $line
        if ($f.Count -lt 3 -or $f[0].StartsWith('#')) { continue }
        $since[$f[0]] = $f[1]
        if ($f[2] -like '*Dockerfile') { $used += $f[0] }
    }
    foreach ($image in compose config --images | Sort-Object -Unique) {
        if (-not $image -or $image -like 'rhythm-*') { continue }
        if ($since.ContainsKey($image)) { $used += $image }
        else { Write-Warning "Not pinned in deploy/image-pins.txt, so no upgrade moves it: $image (an override in .env?)" }
    }
    $today = (Get-Date).ToUniversalTime().Date
    foreach ($image in $used) {
        $day = [datetime]::MinValue
        $dated = [datetime]::TryParseExact($since[$image], 'yyyy-MM-dd', [cultureinfo]::InvariantCulture, 'None', [ref]$day)
        if ($dated -and ($today - $day).TotalDays -gt 90) {
            Write-Warning "Pinned image last checked on $($since[$image]), over 90 days ago: $image. Look for a newer release (deploy/README.md, Pinned images)."
        }
    }
}
# Settings changed: recreate a running engine so it reads them, and the app so it asks again which tools the engine
# offers (a connected source's tiles show at once), and start any new adapter containers.
function apply { if (compose ps -q engine) { dc up -d --wait --force-recreate --no-deps engine app; dc up -d --wait } }

$command = if ($args.Count -gt 0) { $args[0] } else { 'help' }
$rest = @($args | Select-Object -Skip 1)
switch ($command) {
    'init' {
        # A new install's project name from --project-name, so init's own container already runs in that project.
        # Set for this command only: the PowerShell session keeps no variable from it.
        $saved = $env:COMPOSE_PROJECT_NAME
        for ($i = 0; $i -lt $rest.Count; $i++) {
            if ($rest[$i] -eq '--project-name' -and $i + 1 -lt $rest.Count) { $env:COMPOSE_PROJECT_NAME = $rest[$i + 1] }
            elseif ($rest[$i] -like '--project-name=?*') { $env:COMPOSE_PROJECT_NAME = $rest[$i].Substring('--project-name='.Length) }
        }
        # A first init here (no .env yet) that names no project takes Compose's default one, rhythm. Volumes already
        # under that name are another install's data, and this install would start on them: refused until it is named.
        if (-not (Test-Path $envFile) -and -not $env:COMPOSE_PROJECT_NAME -and (docker volume ls -q --filter 'label=com.docker.compose.project=rhythm' 2>$null)) {
            [Console]::Error.WriteLine("A Compose project named rhythm already has volumes on this computer: another install's data. A first init here")
            [Console]::Error.WriteLine("without a project name would start on that data. Nothing was written. Give this install its own project and ports:")
            [Console]::Error.WriteLine("  .\rhythm.ps1 init --project-name <name> --app-port <port> --mcp-port <port>   (cli/README.md, Two installs on one computer)")
            [Console]::Error.WriteLine("To set this checkout up on the existing data on purpose, name it: .\rhythm.ps1 init --project-name rhythm")
            exit 2
        }
        try { compose --profile cli run --rm cli init @rest } finally { $env:COMPOSE_PROJECT_NAME = $saved }
        exit $LASTEXITCODE
    }
    'up' { up }
    'down' {
        # -v deletes the project's volumes: the database and the archive, for good. With no project named (no .env, or
        # none in it, and none in the environment) Compose would take the default one, rhythm, whichever install that is.
        if ($rest | Where-Object { $_ -ceq '-v' -or $_ -ceq '--volumes' -or $_ -cmatch '^-[^-]*v' }) {
            $project = project
            if (-not $project) {
                [Console]::Error.WriteLine("This checkout names no Compose project (deploy/compose/.env is missing, or has no COMPOSE_PROJECT_NAME), so")
                [Console]::Error.WriteLine("down -v would delete the volumes of the default project, rhythm: the database and the archive, whichever")
                [Console]::Error.WriteLine("install they belong to. Nothing was removed. To delete an install's data on purpose, name its project:")
                [Console]::Error.WriteLine('  $env:COMPOSE_PROJECT_NAME = "<name>"; .\rhythm.ps1 down -v')
                exit 2
            }
            [Console]::Error.WriteLine("Removing the project $project with its volumes: the database and the archive, for good.")
        }
        dc down @rest
    }
    'logs' { dc logs @rest }
    'backup' { backup }
    'snapshots' { dc --profile backup run --rm --entrypoint restic backup snapshots }
    'restore' {
        if ($rest.Count -ne 1) { throw 'usage: .\rhythm.ps1 restore <snapshot>   (an id from .\rhythm.ps1 snapshots, or latest)' }
        $folder = "/staging/restore-$(stamp)"
        # The restored folder is a plain copy of the whole database, every scope: it goes once the load is done or has
        # failed (the snapshot keeps it, encrypted). A load that fails leaves the live database as it was (its message
        # says so), so the stack starts again on it.
        # Only a load that ran and failed says so: a snapshot that could not be restored, or a stop that failed, loaded nothing.
        $stopped = $false; $loading = $false
        try {
            dc --profile restore run --rm restore restore $rest[0] $folder
            dc stop engine app
            $stopped = $true
            $loading = $true
            dc --profile restore run --rm restore load $folder
        } catch {
            if ($loading) { Write-Warning "The restore did not load; the load's message above says what it left as it was." }
            throw
        } finally {
            compose --profile restore run --rm --no-deps --entrypoint rm restore -rf $folder
            if ($LASTEXITCODE -ne 0) { Write-Warning "Remove $folder from the backup staging folder yourself: it is a plain copy of the database." }
            if ($stopped) { up }
        }
    }
    'upgrade' {
        $old = version; $new = $old; $how = 'build'
        foreach ($arg in $rest) { if ($arg -eq '--pull') { $how = 'pull' } else { $new = $arg } }
        $saved = $env:RHYTHM_VERSION
        backup
        retag $old "$old-previous"
        try {
            $env:RHYTHM_VERSION = $new
            # --pull: the base images (.NET, Node, Postgres for pg_dump) follow their patch releases only when pulled again.
            if ($how -eq 'pull') { dc pull } else { dc --profile backup build --pull }
            # Recreated even when a tag only moved: Compose does not always notice a rebuilt image under the same tag.
            up --force-recreate
            $text = [IO.File]::ReadAllText($envFile)
            $text = if ($text -match '(?m)^RHYTHM_VERSION=') { $text -replace '(?m)^RHYTHM_VERSION=.*$', "RHYTHM_VERSION=$new" } else { $text.TrimEnd("`n") + "`nRHYTHM_VERSION=$new`n" }
            [IO.File]::WriteAllText($envFile, $text)
            Write-Output "Upgraded to $new. The images before it are tagged $old-previous."
        }
        catch {
            Write-Warning "Upgrade to $new failed: rolling back to $old (the backup above holds the data from before)."
            retag "$old-previous" $old
            $env:RHYTHM_VERSION = $old
            up --force-recreate
            exit 1
        }
        finally { $env:RHYTHM_VERSION = $saved }
        pins
    }
    { $_ -in 'connect', 'disconnect' } { engine-cli $command @rest; if ($rest.Count -gt 0) { apply } }
    { $_ -in 'help', '-h', '--help' } { Get-Content $PSCommandPath -TotalCount 3 | Select-Object -Skip 1 | ForEach-Object { $_ -replace '^# ?', '' }; engine-cli help }
    default { compose --profile cli run --rm cli $command @rest; exit $LASTEXITCODE }
}
