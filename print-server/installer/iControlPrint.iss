; Instalador do iControl Monitor (antigo iControl Print) (Inno Setup 6).
; Nome novo só no que aparece para o usuário: serviço (iControlPrint), executável, pasta e AppId continuam os mesmos,
; para as instalações atuais atualizarem no lugar, sem perder pareamento nem configuração.
; Compilar: iscc /DMyAppVersion=1.0.0 /DPublishDir=..\publish iControlPrint.iss
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#define MyAppName "iControl Monitor"
#define MyExe "IControlPrint.exe"
#define MyService "iControlPrint"
#define MyMonitor "IControlPrintMonitor.exe"

[Setup]
AppId={{6E7C3B0F-2D3A-4C61-9D8B-1C0F1D5E7A21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=iControl Food
AppPublisherURL=https://icontrolfood.com.br
DefaultDirName={autopf}\iControl Print
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=iControlMonitor-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyExe}
SetupIconFile=..\src\IControlPrint\Assets\setup.ico
CloseApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "{#PublishDir}\{#MyExe}"; DestDir: "{app}"; Flags: ignoreversion
; mesmo executável como Monitor da bandeja (o Inno guarda uma cópia só). O Monitor é fechado antes de copiar
; (PrepareToInstall); o restartreplace fica só de reserva, se ainda assim o arquivo estiver preso.
Source: "{#PublishDir}\{#MyExe}"; DestDir: "{app}"; DestName: "{#MyMonitor}"; Flags: ignoreversion restartreplace
; módulo do WhatsApp (Node + Baileys), iniciado pelo serviço só quando o WhatsApp está ligado no sistema
Source: "{#PublishDir}\icontrol-whatsapp.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[UninstallDelete]
; sessão do WhatsApp deste computador
Type: filesandordirs; Name: "{commonappdata}\iControlPrint\whatsapp"

[Dirs]
; PDFs das impressoras de arquivo (Microsoft Print to PDF/XPS) no modo Driver
Name: "C:\iControlPrint\PDF"; Permissions: users-modify

[Icons]
Name: "{autoprograms}\iControl Monitor"; Filename: "{app}\{#MyMonitor}"; Parameters: "--monitor --show"; Comment: "Situação da impressão, ilhas, WhatsApp e testes"
Name: "{autoprograms}\Pasta dos PDFs do iControl Monitor"; Filename: "C:\iControlPrint\PDF"
; atalho na área de trabalho (de todos os usuários), também nas atualizações
Name: "{autodesktop}\iControl Monitor"; Filename: "{app}\{#MyMonitor}"; Parameters: "--monitor --show"; Comment: "Situação da impressão, ilhas, WhatsApp e testes"

[InstallDelete]
; atalhos com o nome antigo (iControl Print)
Type: files; Name: "{autoprograms}\iControl Print Monitor.lnk"
Type: files; Name: "{autoprograms}\Pasta dos PDFs do iControl Print.lnk"

[Registry]
; Monitor da bandeja inicia junto com o Windows para todos os usuários (o serviço já inicia sozinho, sem login)
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "iControlPrintMonitor"; ValueData: """{app}\{#MyMonitor}"" --monitor"; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create {#MyService} binPath= ""\""{app}\{#MyExe}\"""" start= auto DisplayName= ""iControl Monitor"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "config {#MyService} binPath= ""\""{app}\{#MyExe}\"""" start= auto DisplayName= ""iControl Monitor"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description {#MyService} ""iControl Monitor do iControl Food: imprime os pedidos nas ilhas (cozinha, bar, caixa) e envia os avisos de pedido pelo WhatsApp."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#MyService} reset= 86400 actions= restart/5000/restart/10000/restart/30000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failureflag {#MyService} 1"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#MyService}"; Flags: runhidden waituntilterminated
; abre o Monitor (usuário logado) já na tela de status
Filename: "{app}\{#MyMonitor}"; Parameters: "--monitor --show"; Flags: nowait runasoriginaluser skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyMonitor}"; Flags: runhidden waituntilterminated; RunOnceId: "KillMonitor"
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""iControl Monitor"" /f"; Flags: runhidden waituntilterminated; RunOnceId: "DelTask"
Filename: "{app}\{#MyExe}"; Parameters: "--unpair"; Flags: runhidden waituntilterminated; RunOnceId: "Unpair"
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyService}"; Flags: runhidden waituntilterminated; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyService}"; Flags: runhidden waituntilterminated; RunOnceId: "DelSvc"

[Code]
var
  CodePage: TInputQueryWizardPage;
  Paired, ClaimedNow, Installed: Boolean;
  PairedCompany: String;

function RunExe(const Params: String; var Line: String): Integer;
var
  OutFile: String;
  Lines: TArrayOfString;
  Code: Integer;
begin
  ExtractTemporaryFile('{#MyExe}');
  OutFile := ExpandConstant('{tmp}\icp-out.txt');
  DeleteFile(OutFile);
  Line := '';
  if not Exec(ExpandConstant('{tmp}\{#MyExe}'), Params + ' --out "' + OutFile + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  if LoadStringsFromFile(OutFile, Lines) and (GetArrayLength(Lines) > 0) then
    Line := Lines[0];
  Result := Code;
end;

function Field(const S: String; Index: Integer): String;
var
  Rest: String;
  P, I: Integer;
begin
  Rest := S;
  for I := 0 to Index - 1 do
  begin
    P := Pos('|', Rest);
    if P = 0 then begin Result := ''; Exit; end;
    Rest := Copy(Rest, P + 1, Length(Rest));
  end;
  P := Pos('|', Rest);
  if P > 0 then Result := Copy(Rest, 1, P - 1) else Result := Rest;
end;

procedure InitializeWizard;
var
  Line: String;
begin
  // atualização (ou reinstalação) numa máquina já pareada: não pede código de novo
  Paired := RunExe('--status', Line) = 0;
  if Paired then PairedCompany := Field(Line, 1) + ' (' + Field(Line, 2) + ')';
  CodePage := CreateInputQueryPage(wpWelcome,
    'Código de pareamento',
    'Ligue este computador à sua empresa no iControl Food.',
    'No sistema, vá em Configurações > Impressão > Adicionar servidor e digite abaixo o código de 8 caracteres (vale por 10 minutos).');
  CodePage.Add('Código:', False);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = CodePage.ID) and Paired;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Line, Code, Company: String;
begin
  Result := True;
  if CurPageID <> CodePage.ID then Exit;
  Code := Trim(CodePage.Values[0]);
  if Length(Code) < 8 then
  begin
    MsgBox('Digite o código de 8 caracteres gerado no sistema.', mbError, MB_OK);
    Result := False; Exit;
  end;
  if RunExe('--preview ' + Code, Line) <> 0 then
  begin
    MsgBox('Não foi possível validar o código:' + #13#10 + Field(Line, 1) + #13#10#13#10 + 'Confira a internet e o código e tente de novo.', mbError, MB_OK);
    Result := False; Exit;
  end;
  Company := Field(Line, 1) + ' (' + Field(Line, 3) + ')';
  if Field(Line, 2) <> Field(Line, 1) then Company := Company + #13#10 + Field(Line, 2);
  if MsgBox('Conectar a ' + Company + '?' + #13#10#13#10 + 'Os pedidos dessa empresa serão impressos neste computador.', mbConfirmation, MB_YESNO) <> IDYES then
  begin
    Result := False; Exit;
  end;
  if RunExe('--claim ' + Code, Line) <> 0 then
  begin
    MsgBox('Não foi possível concluir o pareamento:' + #13#10 + Field(Line, 1), mbError, MB_OK);
    Result := False; Exit;
  end;
  ClaimedNow := True;
  Paired := True;
  PairedCompany := Company;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  // atualização: para o serviço antes de trocar o executável
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#MyService}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  // módulo do WhatsApp (processo filho do serviço) libera o arquivo
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM icontrol-whatsapp.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  // fecha o Monitor da bandeja para trocar o arquivo já (antes, na atualização silenciosa, só trocava ao reiniciar o Windows);
  // no fim ele é aberto de novo na sessão de quem está logado
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyMonitor}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(2500);
  Result := '';
end;

// Atualização automática roda como SYSTEM (pelo serviço), sem tela: quem abre o Monitor de novo para o usuário é uma
// tarefa agendada do grupo Usuários (abre na sessão de cada usuário logado). XML em ASCII e sem declaração (o schtasks
// recusa UTF-8 declarado).
procedure ReopenMonitor;
var
  Xml, XmlFile: String;
  Code: Integer;
begin
  XmlFile := ExpandConstant('{tmp}\icontrol-monitor-task.xml');
  Xml :=
    '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">' +
    '<Principals><Principal id="Author"><GroupId>S-1-5-32-545</GroupId><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>' +
    '<Settings><MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>' +
    '<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowStartOnDemand>true</AllowStartOnDemand><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>' +
    '<Actions Context="Author"><Exec><Command>"' + ExpandConstant('{app}\{#MyMonitor}') + '"</Command><Arguments>--monitor</Arguments></Exec></Actions>' +
    '</Task>';
  if not SaveStringToFile(XmlFile, Xml, False) then Exit;
  if Exec(ExpandConstant('{sys}\schtasks.exe'), '/create /tn "iControl Monitor" /xml "' + XmlFile + '" /f', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/run /tn "iControl Monitor"', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardSilent then ReopenMonitor;
  if CurStep = ssDone then Installed := True;
end;

procedure DeinitializeSetup;
var
  Line: String;
begin
  // pareou mas desistiu antes de instalar: desfaz o pareamento para não sobrar servidor fantasma
  if ClaimedNow and not Installed then RunExe('--unpair', Line);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := 'Empresa: ' + PairedCompany + NewLine + NewLine +
            'O iControl Monitor será instalado como serviço do Windows: inicia com o computador, sem precisar de login, e reinicia sozinho em caso de falha.' + NewLine + NewLine +
            'O ícone do iControl Monitor fica na bandeja (perto do relógio) para ver a situação, imprimir teste e parear de novo.';
end;
