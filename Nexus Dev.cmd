@echo off
rem Builds and starts Nexus's dev containers, which copy in this folder's code each time they start, and opens Nexus in
rem the browser. It can start Scry's beside them, linked.
setlocal
cd /d "%~dp0"
set COMPOSE_IGNORE_ORPHANS=1

docker info >nul 2>&1
if errorlevel 1 (
  echo Docker is not running. Start Docker Desktop, wait until it says Engine running, then run this again.
  pause
  exit /b 1
)

call :fetch Pluperfect.Jwt ..\..\Pluperfect.Jwt https://github.com/mnwachukwu/Pluperfect.Jwt.git || goto :stopped
call :fetch Pluperfect.Mail ..\..\Pluperfect.Mail https://github.com/mnwachukwu/Pluperfect.Mail.git || goto :stopped

set BOTH=
if exist "..\Scry\compose.dev.yaml" (
  choice /c YN /m "Scry is beside Nexus. Start Scry's dev containers too, linked to Nexus"
  if not errorlevel 2 set BOTH=1
)
set COMPOSE=docker compose -f compose.dev.yaml --env-file .env.dev
if defined BOTH set COMPOSE=docker compose -f compose.dev.both.yaml

call :password . NEXUS_DEV_DB_PASSWORD
if defined BOTH call :password ..\Scry SCRY_DEV_DB_PASSWORD

echo Building and starting the dev containers. The first build takes several minutes.
%COMPOSE% up --build --detach --wait --wait-timeout 600
if errorlevel 1 (
  rem A container whose start failed on a taken port later starts with no network, so none is kept.
  %COMPOSE% rm --force --volumes >nul 2>&1
  echo.
  echo They did not start. The lines above say why. They take the ports development uses, so stop any copy
  echo running from the code, or in Docker, first.
  goto :stopped
)

call :trust nexus-caddy-dev Nexus
if defined BOTH call :trust scry-caddy-dev Scry
call :install nexus-api-dev Nexus
if defined BOTH call :install scry-api-dev Scry
start "" https://localhost:6165
if defined BOTH start "" https://localhost:6160
echo.
echo Nexus is at https://localhost:6165, and its service at https://localhost:6163. Its Mailpit is at
echo http://localhost:6168, and its MySQL answers on 127.0.0.1:6167 for MySQL Workbench, as nexus, with the
echo password in Nexus's .env.dev.
if defined BOTH (
  echo Scry is at https://localhost:6160, and its API at https://localhost:6161. Its Mailpit is at
  echo http://localhost:6162, and its MySQL answers on 127.0.0.1:6166 for MySQL Workbench, as scry, with the password
  echo in Scry's .env.dev.
)
echo To see saved changes, restart a container, in Docker Desktop or with docker restart. After changing a project or
echo package file or compose.dev.yaml, run this again. Docker Desktop shows the dev containers under
echo pluperfect-data-dev, and can stop them.
pause
exit /b 0

:stopped
pause
exit /b 1

rem Clones a checkout the build needs, when it is missing.
:fetch
if exist "%~2" exit /b 0
where git >nul 2>&1
if errorlevel 1 (
  echo %~1 is missing at %~2, and Git is not installed to fetch it. Install Git, or put %~1 there, then run this again.
  exit /b 1
)
echo Fetching %~1 into %~2.
git clone %~3 "%~2"
exit /b %errorlevel%

rem A dev database's password, made the first time and kept in .env.dev beside that product's compose.dev.yaml,
rem apart from the release containers' .env.
:password
if exist "%~1\.env.dev" exit /b 0
powershell -NoProfile -Command "$b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); $p = [Convert]::ToBase64String($b) -replace '[+/=]', ''; [IO.File]::WriteAllText((Join-Path (Resolve-Path '%~1') '.env.dev'), ('%~2=' + $p + [char]10))"
exit /b 0

rem Asks Windows to trust the authority Caddy made for this product's addresses, the first time it meets it.
:trust
set CERT=%TEMP%\pluperfect-%~1.crt
%COMPOSE% cp %~1:/data/caddy/pki/authorities/local/root.crt "%CERT%" >nul 2>&1
if errorlevel 1 (
  echo Could not read %~2's certificate authority, so the browser warns about its addresses.
  exit /b 0
)
powershell -NoProfile -Command "$c = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $env:CERT; if (Test-Path ('Cert:\CurrentUser\Root\' + $c.Thumbprint)) { exit 0 }; Write-Host 'Windows asks once whether to trust %~2 in Docker. Choose Yes, and the browser trusts its addresses.'; certutil -user -addstore Root $env:CERT | Out-Null; exit $LASTEXITCODE"
if errorlevel 1 echo %~2's certificate authority is not trusted, so the browser warns about its addresses.
del "%CERT%" >nul 2>&1
exit /b 0

rem Shows the installer's address, with its install code, while the product is not set up yet.
:install
%COMPOSE% logs --no-log-prefix %~1 | findstr /c:"is not set up yet"
if not errorlevel 1 echo Open the address above to set %~2 up, and enter that install code.
exit /b 0
