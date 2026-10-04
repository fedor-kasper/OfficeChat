@echo off
rem Builds a single OfficeChat.exe that does not require .NET to be installed.
dotnet publish "%~dp0OfficeChat.csproj" -p:PublishProfile=SingleExe
if errorlevel 1 exit /b 1
echo.
echo Done: %~dp0bin\publish\OfficeChat.exe
pause
