@echo off
dotnet build -c Release -nologo || goto error

powershell write-host -fore Green Build finished.
timeout /t 2 /nobreak >nul
exit /b

:error
pause
