@echo off
rem NEXUS - abre a interface (modo somente leitura). Compila antes, se ainda nao existir.
rem Nao precisa de Visual Studio nem de administrador.
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%src\Nexus.Desktop\bin\Release\net10.0-windows\Nexus.Desktop.exe"
if not exist "%EXE%" (
  echo Compilando a interface pela primeira vez...
  dotnet build "%ROOT%src\Nexus.Desktop" -c Release
  if errorlevel 1 (
    echo Falha na compilacao. Veja as mensagens acima.
    pause
    exit /b 1
  )
)
start "" "%EXE%"
endlocal
