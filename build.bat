@echo off
setlocal
cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe

echo Compiling ...
"%CSC%" /nologo /target:winexe /platform:anycpu /codepage:65001 /optimize+ ^
  /out:DeskBoxes.exe ^
  /win32icon:app.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Runtime.Serialization.dll /r:System.Net.dll ^
  src\BoxConfig.cs src\IconLoader.cs src\AutoOrganizer.cs src\OrganizeForm.cs ^

  src\PanelForm.cs src\BalanceData.cs src\BalanceForm.cs src\ShortcutForm.cs ^
  src\HermesData.cs src\HermesBox.cs ^

  src\TrayNotify.cs src\KeepOnDesktop.cs src\Program.cs

if errorlevel 1 (
  echo [FAILED]
  exit /b 1
)

echo [OK] built DeskBoxes.exe
for %%F in (DeskBoxes.exe) do echo    size: %%~zF bytes

rem ---- sign (recompiling always drops the signature) ----
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\sign.ps1"

rem ---------------------------------------------------------------
rem Deploy the runtime copy to %LOCALAPPDATA%\Programs\DeskBoxes.
rem D:\ai_code carries an explicit Medium integrity label, which forces
rem child processes down to Medium and makes Shell_NotifyIcon fail with
rem error 5 (access denied) against a High-integrity explorer.
rem ---------------------------------------------------------------
set DEST=%LOCALAPPDATA%\Programs\DeskBoxes
if not exist "%DEST%" mkdir "%DEST%"
if not exist "%DEST%\data" mkdir "%DEST%\data"

copy /Y DeskBoxes.exe "%DEST%\" >nul 2>&1
if errorlevel 1 (
  echo.
  echo [WARN] exe is in use - close DeskBoxes first, then run build.bat again
) else (
  copy /Y app.ico "%DEST%\" >nul 2>&1
  if not exist "%DEST%\data\history.csv" copy /Y data\history.csv "%DEST%\data\" >nul 2>&1
  echo [OK] deployed to %DEST%
)

endlocal
