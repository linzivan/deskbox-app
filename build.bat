@echo off
rem Build the unified DeskBoxApp (MSVC, static CRT).
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul

echo [release] cl /O2 /MT main.cpp box_balance.cpp box_shortcut.cpp -^> DeskBoxApp.exe
cl /nologo /O2 /EHsc /utf-8 /DUNICODE /D_UNICODE /MT main.cpp box_balance.cpp box_shortcut.cpp /link /subsystem:windows /out:DeskBoxApp.exe
if %ERRORLEVEL% neq 0 ( echo BUILD FAILED & exit /b %ERRORLEVEL% )

rem Sign with the local self-signed code-signing cert so Windows no longer shows
rem the "unknown publisher" warning on startup.
set SIGNTOOL=C:\Program Files (x86)\Windows Kits\10\bin\10.0.22000.0\x64\signtool.exe
if exist "%SIGNTOOL%" (
  "%SIGNTOOL%" sign /n "DeskBox Local" /fd SHA256 "DeskBoxApp.exe" >nul 2>&1
  if %ERRORLEVEL% equ 0 ( echo SIGNED  ^(DeskBox Local^) ) else ( echo SIGN SKIPPED - local cert not found )
) else (
  echo SIGN SKIPPED - signtool.exe not found
)

echo BUILD OK
dir /b DeskBoxApp.exe
