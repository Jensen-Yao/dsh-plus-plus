@echo off
title dsh web
setlocal
cd /d "%~dp0"

rem ------------------------------------------------------------------
rem  dsh web launcher
rem    LAN mode (default): bind all interfaces via lan.patch.yml so any
rem    device on the same network can open the same session and watch
rem    it live - messages, tool output and the thinking stream.
rem
rem    run-dsh-web.cmd                LAN mode, auto-detect address
rem    run-dsh-web.cmd local          loopback only (plain dsh web)
rem    run-dsh-web.cmd 192.168.1.10   LAN mode, show this address
rem    run-dsh-web.cmd 192.168.1.10 8080   ... on port 8080
rem  Config: see run-dsh-web.config.cmd
rem ------------------------------------------------------------------

set "MODE=lan"
set "HOST="
set "PORT=3080"

rem optional config file (sets MODE / HOST / PORT)
if exist "%~dp0run-dsh-web.config.cmd" call "%~dp0run-dsh-web.config.cmd"

rem command-line arguments override config
set "ARG1=%~1"
set "ARG2=%~2"
if /i "%ARG1%"=="local"    set "MODE=local"
if /i "%ARG1%"=="loopback" set "MODE=local"
if /i "%ARG1%"=="lan"      set "MODE=lan"
if /i "%ARG1%"=="auto"     set "MODE=lan"
if not "%ARG1%"=="" if /i not "%ARG1%"=="local" if /i not "%ARG1%"=="loopback" if /i not "%ARG1%"=="lan" if /i not "%ARG1%"=="auto" set "HOST=%ARG1%"
if not "%ARG2%"=="" set "PORT=%ARG2%"

if /i "%MODE%"=="local" goto :local

rem ---- LAN mode -----------------------------------------------------

rem port conflict check
netstat -ano | find ":%PORT% " | find "LISTENING" >nul
if not errorlevel 1 (
  echo.
  echo [dsh] Port %PORT% is already in use.
  echo       Close the old dsh web window first, then run this again.
  pause
  exit /b 1
)

rem auto-detect the LAN IP of the connected adapter
if "%HOST%"=="" (
  echo [dsh] Detecting LAN IP ...
  for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0detect-ip.ps1"`) do set "HOST=%%I"
)
if "%HOST%"=="" goto :local

rem firewall inbound rule (LAN devices need it)
set "RULE=dsh web %PORT%"
if not "%DSH_NO_FIREWALL%"=="1" (
  powershell -NoProfile -Command "if (Get-NetFirewallRule -DisplayName '%RULE%' -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"
  if errorlevel 1 (
    echo [dsh] Firewall rule "%RULE%" is missing.
    net session >nul 2>&1
    if not errorlevel 1 (
      netsh advfirewall firewall add rule name="%RULE%" dir=in action=allow protocol=TCP localport=%PORT% >nul
      echo [dsh] Firewall rule added.
    ) else (
      choice /c YN /m "[dsh] Add it via an admin (UAC) prompt?"
      if not errorlevel 2 (
        powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile -Command New-NetFirewallRule -DisplayName \"%RULE%\" -Direction Inbound -Protocol TCP -LocalPort %PORT% -Action Allow'"
      )
    )
  )
)

echo.
echo ============================================================
echo   dsh web (LAN mode)
echo     local : http://127.0.0.1:%PORT%
echo     LAN   : http://%HOST%:%PORT%
echo   Open the LAN address on any device in the same network and
echo   pick the same session. Messages, tool output and the live
echo   thinking stream stay in sync on every screen.
echo ============================================================
echo.

npx --no-install @deepseek-ai/dsh web --patch "%~dp0lan.patch.yml" --port %PORT%
set "RC=%errorlevel%"
if not "%RC%"=="0" (
  echo.
  echo [dsh] dsh web exited with code %RC%.
  pause
)
exit /b %RC%

:local
netstat -ano | find ":%PORT% " | find "LISTENING" >nul
if not errorlevel 1 (
  echo.
  echo [dsh] Port %PORT% is already in use.
  echo       Close the old dsh web window first, then run this again.
  pause
  exit /b 1
)
echo [dsh] Serving loopback only: http://127.0.0.1:%PORT%
npx --no-install @deepseek-ai/dsh web --port %PORT%
set "RC=%errorlevel%"
if not "%RC%"=="0" (
  echo.
  echo [dsh] dsh web exited with code %RC%.
  pause
)
exit /b %RC%
