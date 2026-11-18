@echo off
rem Builds Nexus's images and starts Nexus in Docker Desktop, then opens it in the browser. With Scry beside this
rem folder, it can start Scry too, linked to Nexus.
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

set FILE=compose.yaml
if exist "..\Scry\compose.yaml" (
  choice /c YN /m "Scry is beside Nexus. Start Scry too, linked to Nexus"
  if not errorlevel 2 set FILE=compose.both.yaml
)

call :password . NEXUS_DB_PASSWORD
if "%FILE%"=="compose.both.yaml" call :password ..\Scry SCRY_DB_PASSWORD

echo Building and starting. The first build takes several minutes.
docker compose -f %FILE% up --build --detach --wait --wait-timeout 600
if errorlevel 1 (
  echo.
  echo It did not start. The lines above say why. If a port is taken, stop the development copy first.
  goto :stopped
)

call :install nexus-api Nexus
if "%FILE%"=="compose.both.yaml" call :install scry-api Scry

echo.
echo Nexus is running at http://localhost:6165. Docker Desktop keeps it running, and can stop it.
start "" http://localhost:6165
if "%FILE%"=="compose.both.yaml" (
  echo Scry is running at http://localhost:6160.
  start "" http://localhost:6160
)
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

rem A database password, made the first time and kept in .env beside that product's compose.yaml.
:password
if exist "%~1\.env" exit /b 0
powershell -NoProfile -Command "$b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); $p = [Convert]::ToBase64String($b) -replace '[+/=]', ''; [IO.File]::WriteAllText((Join-Path (Resolve-Path '%~1') '.env'), ('%~2=' + $p + [char]10))"
exit /b 0

rem Shows the installer's address, with its install code, while the product is not set up yet.
:install
docker compose -f %FILE% logs --no-log-prefix %~1 | findstr /c:"is not set up yet"
if not errorlevel 1 echo Open the address above to set %~2 up, and enter that install code.
exit /b 0
