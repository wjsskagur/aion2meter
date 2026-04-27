@echo off
echo ==========================================
echo  Aion2 Meter - Build Script
echo ==========================================
echo.

dotnet --version > nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET 8 SDK not found.
    echo Please install: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo [1/3] Restoring packages...
dotnet restore Aion2Meter\Aion2Meter.csproj
if errorlevel 1 goto error

echo.
echo [2/3] Building...
dotnet build Aion2Meter\Aion2Meter.csproj -c Release --no-restore
if errorlevel 1 goto error

echo.
echo [3/3] Publishing single exe...
dotnet publish Aion2Meter\Aion2Meter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish\
if errorlevel 1 goto error

echo.
echo ==========================================
echo  Build complete! Output: publish\Aion2Meter.exe
echo ==========================================
echo.
echo [IMPORTANT] Before running:
echo   1. Install Npcap: https://npcap.com
echo      Check: Install Npcap in WinPcap API-compatible Mode
echo   2. Run Aion2Meter.exe as Administrator
echo.
pause
exit /b 0

:error
echo [ERROR] Build failed!
pause
exit /b 1
