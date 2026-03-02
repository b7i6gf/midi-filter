@echo off
echo ====================================
echo  MIDI Filter - Build Script
echo ====================================
echo.

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: .NET SDK not found.
    echo Please install: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo .NET SDK found.
echo Building MidiFilter.exe...
echo.

dotnet publish files\MidiFilter.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o .\publish

if %errorlevel% neq 0 (
    echo.
    echo ERROR. Build failed, see log above.
    pause
    exit /b 1
)

echo.
echo ====================================
echo  Done! MidiFilter.exe now available under:
echo  %cd%\publish\MidiFilter.exe
echo ====================================
pause
