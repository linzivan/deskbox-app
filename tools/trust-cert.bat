@echo off
rem Trust the self-signed cert so Windows stops saying "unknown publisher".
rem No admin needed: installs into CurrentUser stores, one confirm dialog appears.
setlocal
cd /d "%~dp0.."
if not exist "DeskBoxes.cer" (
  echo [TRUST] DeskBoxes.cer not found - run build.bat first
  exit /b 1
)
certutil -addstore -user Root "DeskBoxes.cer"
certutil -addstore -user TrustedPublisher "DeskBoxes.cer"
echo [TRUST] done
endlocal
