@echo off
set ProjectDir=%1
echo on

if exist "%ProjectDir%Renci.SshNet.dll.gz" del "%ProjectDir%Renci.SshNet.dll.gz"
7za a -tgzip -mx9 "%ProjectDir%Renci.SshNet.dll.gz" "%ProjectDir%..\packages\SSH.NET.*\lib\net40\Renci.SshNet.dll" >nul
