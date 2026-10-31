; NEXUS — instalador (Inno Setup 6). Compilado pelo .github/workflows/release.yml; à mão, no Windows:
;   python tools\packaging\publish.py
;   "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" /DAppVersion=0.9.0 /DNumericVersion=0.9.0.0 /DRid=win-x64 ^
;       /DSourceDir=..\..\artifacts\win-x64\NEXUS /DOutputDir=..\..\artifacts tools\packaging\nexus.iss
;
; Decisões (detalhes em docs/08-packaging.md):
;  - Instala em %ProgramFiles%\NEXUS com a ACL herdada de Arquivos de Programas (só administradores alteram). Isso importa:
;    o aplicador elevado (ações de administrador) roda nexus-broker.exe desta pasta. Se o usuário escolher outra pasta e ela
;    for gravável por contas comuns, o instalador avisa.
;  - Desinstalar NÃO apaga dados por padrão. Pergunta (padrão "Não") se remove %LocalAppData%\NEXUS e %ProgramData%\NEXUS; a
;    quarentena (arquivos que o NEXUS tirou do lugar e podem ser do usuário) tem pergunta própria, também com padrão "Não".
;    Em desinstalação silenciosa nada é apagado. A exclusão nunca segue links/junções.
;  - Remove as tarefas agendadas com a marca do NEXUS e o serviço do aplicador (tools\service\Uninstall-Broker.ps1).
;  - Serviço do aplicador: opcional e desmarcado.
;  - Assinatura Authenticode: preparada e desligada (defina SignToolName; ver docs/08-packaging.md).

#ifndef AppVersion
  #define AppVersion "0.9.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.9.0.0"
#endif
#ifndef Rid
  #define Rid "win-x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\" + Rid + "\NEXUS"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif
#if Rid == "win-arm64"
  #define ArchAllowed "arm64"
#else
  #define ArchAllowed "x64compatible"
#endif

[Setup]
AppId={{16A95420-0C76-4948-AB9E-781ED827C4EE}
AppName=NEXUS
AppVersion={#AppVersion}
AppVerName=NEXUS {#AppVersion}
AppPublisher=robinexe
AppComments=Desempenho e manutenção do Windows com diário, confirmação e desfazer.
VersionInfoVersion={#NumericVersion}
VersionInfoProductName=NEXUS
VersionInfoDescription=Instalador do NEXUS
DefaultDirName={autopf}\NEXUS
DefaultGroupName=NEXUS
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=NEXUS-{#AppVersion}-{#Rid}-instalador
SetupIconFile=..\..\src\Nexus.Desktop\Assets\nexus.ico
UninstallDisplayIcon={app}\Nexus.Desktop.exe
UninstallDisplayName=NEXUS {#AppVersion}
WizardStyle=modern
WizardSmallImageFile=icon\wizard-small-55.bmp,icon\wizard-small-110.bmp
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
ShowLanguageDialog=no
#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
#endif

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[CustomMessages]
ptbr.TaskDesktop=Criar atalho na Área de Trabalho
ptbr.TaskService=Instalar o serviço do aplicador (avançado; para ações de administrador sem o aviso do Windows a cada vez)
ptbr.GroupExtras=Opcional:
ptbr.LaunchNexus=Abrir o NEXUS
ptbr.StatusService=Instalando o serviço do aplicador...
ptbr.StatusTasks=Removendo tarefas agendadas do NEXUS...
ptbr.StatusServiceRemove=Removendo o serviço do aplicador...

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:GroupExtras}"; Flags: unchecked
Name: "service"; Description: "{cm:TaskService}"; GroupDescription: "{cm:GroupExtras}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Cópia temporária para o Install-Broker.ps1, que copia o aplicador para %ProgramFiles%\NEXUS\broker: a origem não pode ser
; a pasta-mãe do destino. O Inno guarda uma única cópia de cada arquivo no instalador (não dobra o tamanho).
; {tmp} é apagada sozinha quando o instalador termina.
Source: "{#SourceDir}\*"; DestDir: "{tmp}\nexus-servico"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: ShouldInstallService

[Icons]
Name: "{autoprograms}\NEXUS"; Filename: "{app}\Nexus.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\NEXUS"; Filename: "{app}\Nexus.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Nexus.Desktop.exe"; Description: "{cm:LaunchNexus}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\service\Uninstall-Broker.ps1"""; StatusMsg: "{cm:StatusServiceRemove}"; Flags: runhidden waituntilterminated; RunOnceId: "NexusRemoveService"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\Remove-NexusTasks.ps1"""; StatusMsg: "{cm:StatusTasks}"; Flags: runhidden waituntilterminated; RunOnceId: "NexusRemoveTasks"

[Code]
const
  FILE_ATTRIBUTE_REPARSE_POINT = $400;
  SERVICE_NAME = 'NexusBroker';

var
  RemoveData: Boolean;
  RemoveQuarantine: Boolean;
  ServiceWanted: Boolean;  { decidido uma vez em ssInstall (Check pode ser chamado várias vezes) }

function PowerShell(): String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function ServiceExists(): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query ' + SERVICE_NAME, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

{ Instala/atualiza o serviço se a opção foi marcada ou se ele já existe (atualização: o serviço precisa da mesma versão). }
function ShouldInstallService(): Boolean;
begin
  Result := ServiceWanted;
end;

function IsDigitsAndDashes(S: String): Boolean;
var
  I: Integer;
begin
  Result := Length(S) > 0;
  for I := 1 to Length(S) do
    if not (((S[I] >= '0') and (S[I] <= '9')) or (S[I] = '-')) then
      Result := False;
end;

{ SID de quem iniciou o instalador (antes da elevação): o serviço só aceita pedidos dessa conta. Se não der para saber
  (ex.: elevação com a senha de OUTRA conta, que não escreve na pasta temporária do instalador), devolve ''. }
function OriginalUserSid(): String;
var
  Code, P: Integer;
  OutFile: String;
  Raw: AnsiString;
  S: String;
begin
  Result := '';
  OutFile := ExpandConstant('{tmp}\nexus-sid.txt');
  if not ExecAsOriginalUser(ExpandConstant('{cmd}'), '/c whoami /user /fo csv /nh > "' + OutFile + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Exit;
  if (Code <> 0) or not LoadStringFromFile(OutFile, Raw) then
    Exit;
  S := String(Raw);
  P := Pos('"S-1-5-21-', S);
  if P = 0 then
    Exit;
  S := Copy(S, P + 1, Length(S));
  P := Pos('"', S);
  if P = 0 then
    Exit;
  S := Copy(S, 1, P - 1);
  if (Length(S) > 12) and (Length(S) < 100) and IsDigitsAndDashes(Copy(S, 5, Length(S))) then
    Result := S;
end;

procedure InstallService();
var
  Sid, Params: String;
  Code: Integer;
begin
  Sid := OriginalUserSid();
  if Sid = '' then
  begin
    SuppressibleMsgBox('Não foi possível identificar a sua conta de usuário, então o serviço do aplicador NÃO foi instalado.' + #13#10#13#10 +
      'O NEXUS funciona sem ele (pede o aviso do Windows nas ações de administrador). Para instalar depois, rode o instalador de novo ' +
      'a partir da SUA conta (clique duplo e confirme o aviso do Windows) e marque a opção do serviço.',
      mbInformation, MB_OK, IDOK);
    Exit;
  end;
  WizardForm.StatusLabel.Caption := CustomMessage('StatusService');
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\tools\service\Install-Broker.ps1') + '"' +
            ' -Source "' + ExpandConstant('{tmp}\nexus-servico') + '" -AllowSid ' + Sid + ' -AllowHost';
  if not Exec(PowerShell(), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    SuppressibleMsgBox('A instalação do serviço do aplicador falhou (código ' + IntToStr(Code) + '). O NEXUS funciona sem ele.' + #13#10 +
      'Detalhes no log do instalador (opção /LOG) e em tools\service\README.md.', mbError, MB_OK, IDOK)
  else
    Log('Serviço do aplicador instalado para ' + Sid);
end;

{ Pasta escolhida protegida? (Test-NexusFolderSafety.ps1: 0 = só administradores alteram.) }
procedure CheckFolderSafety();
var
  Code: Integer;
begin
  if Exec(PowerShell(), '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\tools\Test-NexusFolderSafety.ps1') + '" -Path "' + ExpandConstant('{app}') + '" -Quiet',
          '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    Log('Test-NexusFolderSafety: ' + IntToStr(Code));
    if Code = 1 then
      SuppressibleMsgBox('Atenção: contas sem privilégio de administrador podem alterar arquivos em' + #13#10 + ExpandConstant('{app}') + #13#10#13#10 +
        'As ações de administrador do NEXUS rodam programas desta pasta com privilégio elevado. Recomendado: reinstalar na pasta padrão (Arquivos de Programas).',
        mbError, MB_OK, IDOK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    ServiceWanted := WizardIsTaskSelected('service') or ServiceExists()
  else if CurStep = ssPostInstall then
  begin
    CheckFolderSafety();
    if ShouldInstallService() then
      InstallService();
  end;
end;

{ ---------- Desinstalação ---------- }

function NexusRunning(): Boolean;
var
  Code: Integer;
begin
  { Processos do NEXUS na sessão do usuário (o serviço roda na sessão 0 e é parado pelo Uninstall-Broker.ps1). }
  Result := Exec(PowerShell(), '-NoProfile -ExecutionPolicy Bypass -Command "if (Get-Process -Name ''Nexus.Desktop'',''nexus-broker'',''nexus'',''nexus-recover'' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 }) { exit 1 } else { exit 0 }"',
                 '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 1);
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  while Result and NexusRunning() do
  begin
    if UninstallSilent() then
    begin
      Log('NEXUS em execução; desinstalação silenciosa cancelada.');
      Result := False;
    end
    else
      Result := MsgBox('Feche o NEXUS (interface e janelas do nexus) antes de desinstalar.' + #13#10#13#10 + 'Já fechou? Clique em OK para continuar.',
                       mbInformation, MB_OKCANCEL) = IDOK;
  end;
end;

{ Conta arquivos sem seguir links/junções (limite para não demorar). }
function CountFiles(Dir: String; Limit: Integer): Integer;
var
  F: TFindRec;
begin
  Result := 0;
  if FindFirst(AddBackslash(Dir) + '*', F) then
  try
    repeat
      if (F.Name <> '.') and (F.Name <> '..') and ((F.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) = 0) then
      begin
        if (F.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          Result := Result + CountFiles(AddBackslash(Dir) + F.Name, Limit - Result)
        else
          Result := Result + 1;
      end;
    until (Result >= Limit) or not FindNext(F);
  finally
    FindClose(F);
  end;
end;

{ Apaga uma árvore SEM seguir links/junções: um link é removido como link, o destino dele nunca é tocado.
  (%LocalAppData%\NEXUS é gravável pelo usuário e o desinstalador roda como administrador.) }
procedure SafeDelTree(Path: String);
var
  F: TFindRec;
  Attr: Integer;
begin
  Attr := GetFileAttributes(Path);
  if Attr = -1 then
    Exit;
  if (Attr and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
  begin
    if (Attr and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      RemoveDir(Path)
    else
      DeleteFile(Path);
    Exit;
  end;
  if (Attr and FILE_ATTRIBUTE_DIRECTORY) = 0 then
  begin
    DeleteFile(Path);
    Exit;
  end;
  if FindFirst(AddBackslash(Path) + '*', F) then
  try
    repeat
      if (F.Name <> '.') and (F.Name <> '..') then
        SafeDelTree(AddBackslash(Path) + F.Name);
    until not FindNext(F);
  finally
    FindClose(F);
  end;
  RemoveDir(Path);
end;

procedure DeleteDataDir(Dir: String; KeepQuarantine: Boolean);
var
  F: TFindRec;
begin
  if not DirExists(Dir) then
    Exit;
  if (GetFileAttributes(Dir) and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
  begin
    Log('Pasta de dados é link/junção; não removida: ' + Dir);
    Exit;
  end;
  if FindFirst(AddBackslash(Dir) + '*', F) then
  try
    repeat
      if (F.Name <> '.') and (F.Name <> '..') and not (KeepQuarantine and (CompareText(F.Name, 'quarantine') = 0)) then
        SafeDelTree(AddBackslash(Dir) + F.Name);
    until not FindNext(F);
  finally
    FindClose(F);
  end;
  RemoveDir(Dir); { só sai se ficou vazia }
  Log('Dados removidos: ' + Dir + ' (quarentena mantida: ' + IntToStr(Ord(KeepQuarantine)) + ')');
end;

{ nexus-recover scan: 0 = nada a recuperar; 3 = diário não existe; outro = há o que recuperar ou não deu para conferir. }
function JournalNeedsRecovery(Journal: String): Boolean;
var
  Code: Integer;
begin
  Result := False;
  if not FileExists(Journal) then
    Exit;
  if not Exec(ExpandConstant('{app}\nexus-recover.exe'), 'scan --journal "' + Journal + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := True
  else
    Result := (Code <> 0) and (Code <> 3);
  Log('nexus-recover scan ' + Journal + ': ' + IntToStr(Code));
end;

procedure AskAboutData();
var
  Local, Common: String;
  Quarantined: Integer;
begin
  Local := ExpandConstant('{localappdata}\NEXUS');
  Common := ExpandConstant('{commonappdata}\NEXUS');
  if not DirExists(Local) and not DirExists(Common) then
    Exit;

  if JournalNeedsRecovery(Local + '\journal.db') or JournalNeedsRecovery(Common + '\journal.db') then
  begin
    MsgBox('Há operações do NEXUS interrompidas que ainda podem ser recuperadas, então os dados do NEXUS serão MANTIDOS.' + #13#10#13#10 +
      'Para recuperar: reinstale o NEXUS e abra o Histórico, ou rode "nexus-recover recover".' + #13#10 + Local + #13#10 + Common,
      mbInformation, MB_OK);
    Exit;
  end;

  RemoveData := MsgBox('Remover também os dados do NEXUS?' + #13#10#13#10 +
    Local + #13#10 + '   configurações, relatórios, medições, histórico das ações' + #13#10 +
    Common + #13#10 + '   histórico das ações de administrador' + #13#10#13#10 +
    'O histórico é o que permite DESFAZER o que o NEXUS alterou. Sem ele, as alterações continuam no PC, mas o NEXUS não consegue mais desfazê-las.' + #13#10#13#10 +
    'Recomendado: Não (os dados ficam; reinstalar o NEXUS recupera tudo).' + #13#10 +
    'Só os dados desta conta (' + GetUserNameString() + ') e os do computador são removidos.',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  if not RemoveData then
    Exit;

  Quarantined := CountFiles(Local + '\quarantine', 100000) + CountFiles(Common + '\quarantine', 100000);
  if Quarantined = 0 then
  begin
    RemoveQuarantine := True;
    Exit;
  end;
  RemoveQuarantine := MsgBox('A quarentena do NEXUS tem ' + IntToStr(Quarantined) + ' arquivo(s) que o NEXUS tirou do lugar (limpezas, sobras de programas, cópias de segurança). Eles podem ser SEUS.' + #13#10#13#10 +
    'Apagar a quarentena DE VEZ? Isso não pode ser desfeito.' + #13#10#13#10 +
    'Recomendado: Não. A pasta "quarantine" fica em' + #13#10 + Local + #13#10 + Common + #13#10 +
    'e cada item tem um manifest.json com o caminho original, para devolver à mão se precisar.',
    mbCriticalError, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveData := False;
    RemoveQuarantine := False;
    if not UninstallSilent() then
      AskAboutData();
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    if RemoveData then
    begin
      DeleteDataDir(ExpandConstant('{localappdata}\NEXUS'), not RemoveQuarantine);
      DeleteDataDir(ExpandConstant('{commonappdata}\NEXUS'), not RemoveQuarantine);
    end;
  end;
end;
