@echo off
rem Builds a single self-contained AtcWatcher.exe (no .NET install needed on the target PC).
cd /d "%~dp0"
dotnet publish AtcWatcher\AtcWatcher.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none ^
  -o dist
if errorlevel 1 (echo Publish failed & pause & exit /b 1)
echo.
echo Done: %~dp0dist\AtcWatcher.exe
pause
