@echo off
rem NEXUS - abre o NEXUS.
rem   "Abrir NEXUS.cmd"       abre o NEXUS INSTALADO (Arquivos de Programas), onde as acoes de administrador funcionam.
rem                           Se nao houver instalacao, compila (rapido se nada mudou) e abre a copia de desenvolvimento.
rem   "Abrir NEXUS.cmd" dev   sempre compila e abre a copia de desenvolvimento (src\Nexus.Desktop\bin\Release).
rem Instalacao: chave de desinstalacao do instalador (AppId do tools\packaging\nexus.iss) ou %ProgramFiles%\NEXUS.
rem Nao precisa de Visual Studio nem de administrador. Mudancas de sistema pedem permissao ao Windows na hora.
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%src\Nexus.Desktop\bin\Release\net10.0-windows\Nexus.Desktop.exe"
set "INSTALLED="
for /f "tokens=2,*" %%a in ('reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{16A95420-0C76-4948-AB9E-781ED827C4EE}_is1" /v InstallLocation /reg:64 2^>nul ^| find "REG_SZ"') do set "INSTALLED=%%b"
if defined INSTALLED if not "%INSTALLED:~-1%"=="\" set "INSTALLED=%INSTALLED%\"
if defined INSTALLED if not exist "%INSTALLED%Nexus.Desktop.exe" set "INSTALLED="
if not defined INSTALLED if exist "%ProgramFiles%\NEXUS\Nexus.Desktop.exe" set "INSTALLED=%ProgramFiles%\NEXUS\"

if /i "%~1"=="dev" goto dev
if not defined INSTALLED goto notinstalled
echo Abrindo o NEXUS instalado: "%INSTALLED%Nexus.Desktop.exe". Para a copia de desenvolvimento: "Abrir NEXUS.cmd" dev
start "" "%INSTALLED%Nexus.Desktop.exe"
exit /b 0

:notinstalled
echo NEXUS nao esta instalado: abrindo a copia de desenvolvimento (acoes de administrador so funcionam no instalado).
goto build

:dev
if defined INSTALLED echo Copia de desenvolvimento: acoes de administrador nao rodam aqui (pasta gravavel); use o NEXUS instalado.

:build
echo Preparando o NEXUS...
dotnet build "%ROOT%src\Nexus.Desktop" -c Release -v q -nologo > "%TEMP%\nexus-build.txt" 2>&1
if errorlevel 1 goto buildfailed
start "" "%EXE%"
exit /b 0

:buildfailed
type "%TEMP%\nexus-build.txt"
echo Falha na compilacao. Veja as mensagens acima.
pause
exit /b 1
