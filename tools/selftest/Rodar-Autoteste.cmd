@echo off
rem ============================================================================================================
rem NEXUS - autoteste no PC real (SOMENTE LEITURA: nada e aplicado, apagado ou configurado).
rem
rem   Dois cliques neste arquivo. Ele:
rem     1. acha o NEXUS: o repositorio compilado (compila antes, se o .NET SDK existir) ou a pasta instalada;
rem     2. abre a interface em modo autoteste (Nexus.Desktop.exe --selftest-ui): cada tela e aberta sozinha,
rem        com imagem de cada uma; a janela fecha sozinha no fim (2 a 6 minutos; nao clique nela);
rem     3. roda "nexus selftest" (as leituras de todos os modulos);
rem     4. grava tudo em %LocalAppData%\NEXUS\autoteste\<data>\ e abre o relatorio.html.
rem
rem   Opcoes (na linha de comando):
rem     admin                  roda como administrador (o Windows pergunta). Algumas leituras so funcionam assim.
rem     sem-interface          pula o autoteste da interface (so as leituras dos modulos)
rem     --with-benchmarks      tambem mede CPU e memoria por alguns segundos
rem     --with-windows-update  tambem procura drivers no Windows Update (lento)
rem   Exemplo: Rodar-Autoteste.cmd admin --with-benchmarks
rem ============================================================================================================
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

set "HERE=%~dp0"
set "SCRIPT=%~f0"
set "ADMIN="
set "NOUI="
set "EXTRA="
:args
if "%~1"=="" goto args_done
if /i "%~1"=="admin" (set "ADMIN=1") else if /i "%~1"=="/admin" (set "ADMIN=1") else if /i "%~1"=="--elevated" (set "ELEVATED=1") else if /i "%~1"=="sem-interface" (set "NOUI=1") else (set "EXTRA=!EXTRA! %1")
shift
goto args
:args_done

if defined ADMIN if not defined ELEVATED (
  echo Pedindo permissao de administrador ao Windows...
  set "PASS=--elevated"
  if defined NOUI set "PASS=!PASS! sem-interface"
  set "PASS=!PASS!!EXTRA!"
  powershell -NoProfile -Command "Start-Process -FilePath $env:ComSpec -ArgumentList ('/c \"\"' + $env:SCRIPT + '\" ' + $env:PASS + '\"') -Verb RunAs"
  if errorlevel 1 echo O Windows nao deu a permissao. Rode de novo sem "admin" para o autoteste comum.
  exit /b 0
)

rem ---------------------------------------------------------------- onde esta o NEXUS
set "BIN="
for %%R in ("%HERE%..\..") do set "ROOT=%%~fR"
if exist "%ROOT%\Nexus.slnx" (
  where dotnet >nul 2>&1
  if not errorlevel 1 (
    echo Compilando o NEXUS ^(rapido se nada mudou^)...
    dotnet build "%ROOT%\src\Nexus.Desktop" -c Release -v q -nologo > "%TEMP%\nexus-autoteste-build.txt" 2>&1
    if errorlevel 1 (
      type "%TEMP%\nexus-autoteste-build.txt"
      echo Falha na compilacao. Veja as mensagens acima.
      pause
      exit /b 1
    )
  )
  if exist "%ROOT%\src\Nexus.Desktop\bin\Release\net10.0-windows\Nexus.Desktop.exe" set "BIN=%ROOT%\src\Nexus.Desktop\bin\Release\net10.0-windows"
)
if not defined BIN if exist "%HERE%Nexus.Desktop.exe" set "BIN=%HERE:~0,-1%"
if not defined BIN if exist "%ProgramFiles%\NEXUS\Nexus.Desktop.exe" set "BIN=%ProgramFiles%\NEXUS"
if not defined BIN if exist "%LocalAppData%\Programs\NEXUS\Nexus.Desktop.exe" set "BIN=%LocalAppData%\Programs\NEXUS"
if not defined BIN (
  echo Nao achei o NEXUS: nem o repositorio compilado, nem a pasta instalada ^(%ProgramFiles%\NEXUS^).
  echo Instale o .NET 10 SDK para compilar a partir do repositorio, ou instale o NEXUS.
  pause
  exit /b 3
)
if not exist "%BIN%\nexus.exe" (
  echo Achei a interface em "%BIN%", mas nao o nexus.exe ao lado dela.
  pause
  exit /b 3
)

rem ---------------------------------------------------------------- pasta do relatorio
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "STAMP=%%i"
set "OUT=%LocalAppData%\NEXUS\autoteste\%STAMP%"
mkdir "%OUT%" >nul 2>&1
echo.
echo NEXUS: %BIN%
echo Relatorio: %OUT%
echo Somente leitura: nada sera alterado no seu PC.
echo.

rem ---------------------------------------------------------------- 1) interface
set "UIARG="
set "UICODE=-"
if not defined NOUI (
  echo [1/2] Autoteste da interface: a janela do NEXUS vai abrir e passar por todas as telas sozinha.
  echo       Nao clique nela; ela fecha sozinha no fim ^(2 a 6 minutos^).
  start "" /wait "%BIN%\Nexus.Desktop.exe" --selftest-ui "%OUT%\ui.json"
  set "UICODE=!errorlevel!"
  if exist "%OUT%\ui.json" (set "UIARG=--ui "%OUT%\ui.json"") else (echo       A interface nao gravou ui.json ^(codigo !UICODE!^).)
) else (
  echo [1/2] Autoteste da interface: pulado ^(sem-interface^).
)

rem ---------------------------------------------------------------- 2) leituras dos modulos
echo [2/2] Leituras de todos os modulos ^(1 a 4 minutos^)...
"%BIN%\nexus.exe" selftest --json "%OUT%\relatorio.json" --html "%OUT%\relatorio.html" !UIARG! !EXTRA! > "%OUT%\relatorio.txt" 2>&1
set "CLICODE=%errorlevel%"
type "%OUT%\relatorio.txt"
echo.
echo Interface: codigo %UICODE%   Modulos: codigo %CLICODE%   ^(0 = sem falhas^)
echo Arquivos em %OUT%
if exist "%OUT%\relatorio.html" start "" "%OUT%\relatorio.html"
echo.
pause
endlocal & exit /b %CLICODE%
