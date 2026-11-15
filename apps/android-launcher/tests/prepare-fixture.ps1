$ErrorActionPreference = 'Stop'
if (-not $env:JAVA_HOME) { throw 'Set JAVA_HOME to Java 17 for this process first.' }
$module = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fixture = Join-Path $module 'build\fixture'
$resources = Join-Path $module 'launcher\build\fixture-res\raw'
New-Item -ItemType Directory -Force $fixture, $resources | Out-Null
$keytool = Join-Path $env:JAVA_HOME 'bin\keytool.exe'
$keystore = Join-Path $fixture 'server.p12'
if (-not (Test-Path -LiteralPath $keystore)) {
    & $keytool -genkeypair -alias fixture -keyalg RSA -keysize 2048 -validity 7 -dname 'CN=Disposable Rhythm Fixture' -ext 'SAN=IP:10.0.2.2,IP:127.0.0.1' -ext 'BC=ca:true' -storetype PKCS12 -keystore $keystore -storepass fixture-only -keypass fixture-only
    if ($LASTEXITCODE -ne 0) { throw 'Fixture certificate generation failed.' }
}
& $keytool -exportcert -alias fixture -keystore $keystore -storepass fixture-only -file (Join-Path $resources 'fixture_cert.der')
if ($LASTEXITCODE -ne 0) { throw 'Fixture certificate export failed.' }
