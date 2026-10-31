@echo off
rem NEXUS - compila (rapido se nada mudou) e abre a interface.
rem Nao precisa de Visual Studio nem de administrador. Mudancas de sistema pedem permissao ao Windows na hora.
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%src\Nexus.Desktop\bin\Release\net10.0-windows\Nexus.Desktop.exe"
echo Preparando o NEXUS...
dotnet build "%ROOT%src\Nexus.Desktop" -c Release -v q -nologo > "%TEMP%\nexus-build.txt" 2>&1
if errorlevel 1 (
  type "%TEMP%\nexus-build.txt"
  echo Falha na compilacao. Veja as mensagens acima.
  pause
  exit /b 1
)
start "" "%EXE%"
endlocal
