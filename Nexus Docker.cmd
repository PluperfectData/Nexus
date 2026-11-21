@echo off
rem Builds Nexus's images and starts Nexus in Docker Desktop, then opens it in the browser. With Scry beside this
rem folder, it can start Scry too, linked to Nexus.
rem From a release's zip, which has no Dockerfile, it loads the images the zip carries, or downloads them.
setlocal
cd /d "%~dp0"
set COMPOSE_IGNORE_ORPHANS=1

docker info >nul 2>&1
if errorlevel 1 (
  echo Docker is not running. Start Docker Desktop, wait until it says Engine running, then run this again.
  pause
  exit /b 1
)

if exist Dockerfile (
  call :fetch Pluperfect.Jwt ..\..\Pluperfect.Jwt https://github.com/mnwachukwu/Pluperfect.Jwt.git || goto :stopped
  call :fetch Pluperfect.Mail ..\..\Pluperfect.Mail https://github.com/mnwachukwu/Pluperfect.Mail.git || goto :stopped
)

set FILE=compose.yaml
if exist "..\Scry\compose.yaml" (
  choice /c YN /m "Scry is beside Nexus. Start Scry too, linked to Nexus"
  if not errorlevel 2 set FILE=compose.both.yaml
)

call :password . NEXUS_DB_PASSWORD
if "%FILE%"=="compose.both.yaml" call :password ..\Scry SCRY_DB_PASSWORD

set BUILD=
if exist Dockerfile (
  echo Building and starting. The first build takes several minutes.
  set BUILD=--build
) else (
  call :images || goto :stopped
)
docker compose -f %FILE% up %BUILD% --detach --wait --wait-timeout 600
if errorlevel 1 (
  echo.
  echo It did not start. The lines above say why. If a port is taken, stop the development copy first.
  goto :stopped
)

call :trust nexus-web Nexus
if "%FILE%"=="compose.both.yaml" call :trust scry-web Scry

call :install nexus-api Nexus
if "%FILE%"=="compose.both.yaml" call :install scry-api Scry

echo.
echo Nexus is running at https://localhost:6165. Docker Desktop keeps it running, and can stop it.
echo Nexus's Mailpit, which keeps the mail Nexus sends until Postmark is set up, is at http://localhost:6168.
start "" https://localhost:6165
if "%FILE%"=="compose.both.yaml" (
  echo Scry is running at https://localhost:6160.
  echo Scry's Mailpit, which keeps the mail Scry sends until Postmark is set up, is at http://localhost:6162.
  start "" https://localhost:6160
)
pause
exit /b 0

:stopped
pause
exit /b 1

rem A release's zip carries each product's x86-64 images; anywhere else, or without them, they are downloaded.
:images
call :missing
if not defined MISSING (
  echo Starting.
  exit /b 0
)
set ARCH=
for /f "delims=" %%a in ('docker info --format "{{.Architecture}}"') do set ARCH=%%a
if /i "%ARCH%"=="x86_64" (
  if exist nexus-images.tar (
    echo Loading Nexus from nexus-images.tar, then starting. This takes a few minutes the first time.
    docker load -i nexus-images.tar || exit /b 1
  )
  if "%FILE%"=="compose.both.yaml" if exist ..\Scry\scry-images.tar (
    echo Loading Scry from ..\Scry\scry-images.tar.
    docker load -i ..\Scry\scry-images.tar || exit /b 1
  )
)
call :missing
if not defined MISSING exit /b 0
echo Downloading and starting. The first download takes several minutes.
docker compose -f %FILE% pull
if errorlevel 1 (
  echo.
  echo It did not download. If the lines above say denied or unauthorized, sign in once with
  echo docker login ghcr.io, as docs/docker.md shows, then run this again.
  exit /b 1
)
exit /b 0

rem Sets MISSING when an image this stack runs from the registry is not loaded.
:missing
set MISSING=
for /f "delims=" %%i in ('docker compose -f %FILE% config --images ^| findstr /b /c:"ghcr.io/"') do (
  docker image inspect %%i >nul 2>&1 || set MISSING=1
)
exit /b 0

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

rem A database password, made the first time and kept in .env beside that product's compose.yaml.
:password
if exist "%~1\.env" exit /b 0
powershell -NoProfile -Command "$b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); $p = [Convert]::ToBase64String($b) -replace '[+/=]', ''; [IO.File]::WriteAllText((Join-Path (Resolve-Path '%~1') '.env'), ('%~2=' + $p + [char]10))"
exit /b 0

rem Asks Windows to trust the authority Caddy made for this product's addresses, the first time it meets it.
:trust
set CERT=%TEMP%\pluperfect-%~1.crt
docker compose -f %FILE% cp %~1:/data/caddy/pki/authorities/local/root.crt "%CERT%" >nul 2>&1
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
docker compose -f %FILE% logs --no-log-prefix %~1 | findstr /c:"is not set up yet"
if not errorlevel 1 echo Open the address above to set %~2 up, and enter that install code.
exit /b 0
