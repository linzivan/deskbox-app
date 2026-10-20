@echo off
rem Build the unified DeskBoxApp (MSVC, static CRT).
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul

echo [release] cl /O2 /MT main.cpp box_balance.cpp box_shortcut.cpp -^> DeskBoxApp.exe
cl /nologo /O2 /EHsc /utf-8 /DUNICODE /D_UNICODE /MT main.cpp box_balance.cpp box_shortcut.cpp /link /subsystem:windows /out:DeskBoxApp.exe
if %ERRORLEVEL% neq 0 ( echo BUILD FAILED & exit /b %ERRORLEVEL% )
echo BUILD OK
dir /b DeskBoxApp.exe
