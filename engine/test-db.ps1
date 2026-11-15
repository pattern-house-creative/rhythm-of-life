# engine/test-db.ps1: the engine's whole test run, database tests included, in PowerShell (engine/test-db.sh in sh).
# Starts a throwaway pgvector container on a random local port, runs `dotnet test` with RHYTHM_TEST_DB pointing at
# it, and removes the container again, also when a test fails or the run is stopped. Plain `dotnet test engine` skips
# the database tests.
#
#   .\engine\test-db.ps1 [dotnet test arguments, for example --filter DatabaseTests]
#
# RHYTHM_TEST_POSTGRES_IMAGE picks another image (default the one compose runs, read from its RHYTHM_POSTGRES_IMAGE
# default), RHYTHM_TEST_DB_PORT a fixed local port and RHYTHM_TEST_CONTAINER the container's name. The tests get the
# name as RHYTHM_TEST_DB_CONTAINER, for the backup and restore test's pg_dump and pg_restore. Needs Docker; nothing else
# on the machine is touched.
$ErrorActionPreference = 'Stop'
$compose = Get-Content -Raw (Join-Path $PSScriptRoot '../deploy/compose/engine.compose.yaml')
$image = if ($env:RHYTHM_TEST_POSTGRES_IMAGE) { $env:RHYTHM_TEST_POSTGRES_IMAGE }
    elseif ($compose -match '\$\{RHYTHM_POSTGRES_IMAGE:-([^}]+)\}') { $Matches[1] }
    else { throw 'No Postgres image in deploy/compose/engine.compose.yaml; set RHYTHM_TEST_POSTGRES_IMAGE.' }
$name = if ($env:RHYTHM_TEST_CONTAINER) { $env:RHYTHM_TEST_CONTAINER } else { "rhythm-engine-test-$PID-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())" }
$bytes = New-Object byte[] 16
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
$code = 1
try {
    # Each test class migrates a database of its own, all at once, every migration in one transaction: the default lock
    # table (64 locks per connection) runs out ("out of shared memory", 53200), so the test server keeps more.
    docker run -d --rm --name $name -e "POSTGRES_PASSWORD=$password" -p "127.0.0.1:$($env:RHYTHM_TEST_DB_PORT):5432" $image -c max_locks_per_transaction=256 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'docker run failed; is Docker running?' }
    $port = ((docker port $name 5432/tcp) | Select-Object -First 1) -replace '.*:', ''
    # The image's first start runs its setup on a local socket; the TCP listener means it is done.
    $tries = 0
    while ($true) {
        docker exec $name pg_isready -q -U postgres -h 127.0.0.1 | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        if (++$tries -ge 60) { throw 'The test database did not start.' }
        Start-Sleep -Seconds 1
    }
    $env:RHYTHM_TEST_DB = "Host=127.0.0.1;Port=$port;Username=postgres;Password=$password"
    $env:RHYTHM_TEST_DB_CONTAINER = $name
    dotnet test $PSScriptRoot @args
    $code = $LASTEXITCODE
}
finally {
    Remove-Item Env:RHYTHM_TEST_DB -ErrorAction SilentlyContinue
    Remove-Item Env:RHYTHM_TEST_DB_CONTAINER -ErrorAction SilentlyContinue
    # -v: the data volume too, which rm -f alone leaves behind (as test-db.sh does).
    docker rm -f -v $name 2>&1 | Out-Null
}
exit $code
