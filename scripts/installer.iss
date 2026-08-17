; Inno Setup Installer Script for MarklifeWin

#define AppName "Marklife X2 Printer"
#define AppVersion "1.0.0"
#define AppExeName "MarklifeWin.exe"
#define AppSourceDir "..\bin\Release\net8.0-windows\win-x64"
#define MyAppId "{{7B8F9D2A-1F3C-4E5B-9A7D-6C8B0A2F1E3D}"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={commonpf64}\Marklife X2 Printer
DefaultGroupName=Marklife X2 Printer
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
OutputDir=../dist
OutputBaseFilename=MarklifeX2Printer_Setup
PrivilegesRequired=admin
WizardStyle=modern
SetupIconFile=..\Resources\main.ico
UninstallDisplayIcon={app}\{#AppExeName}
AppId={#MyAppId}
DisableDirPage=auto
DisableProgramGroupPage=auto
DirExistsWarning=no
ShowLanguageDialog=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\Driver\IPP\Marklife_X2.gpd"; Flags: dontcopy

[Icons]
Name: "{autodesktop}\Marklife X2 Printer"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\Marklife X2 Printer"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Удалить программу"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Запустить программу"; Flags: postinstall nowait skipifsilent

[Code]
var
  UpgradePage: TWizardPage;
  RadioReinstall: TNewRadioButton;
  RadioUninstall: TNewRadioButton;
  IsUpgrade: Boolean;
  UninstPath: string;
  ResultCode: Integer;

// Получение пути к деинсталлятору из реестра
function GetUninstallerPath(): string;
var
  RegKey: string;
  AppId: String;
begin
  Result := '';
  AppId := '{#MyAppId}'
  StringChange(AppId, '{{', '{');
  RegKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + AppId + '_is1';
  
  RegQueryStringValue(HKLM, RegKey, 'UninstallString', Result);
end;

// Проверка наличия установки через реестр
function CheckIfInstalled(): Boolean;
var AppId: String;
begin
  AppId := '{#MyAppId}'
  StringChange(AppId, '{{', '{');
  Result := RegKeyExists(HKEY_LOCAL_MACHINE, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + AppId + '_is1');
end;

// Создание страницы с радиокнопками
procedure CreateUpgradePage();
begin
  UpgradePage := CreateCustomPage(wpWelcome, 'Программа уже установлена', 'Что вы хотите сделать?');
  
  RadioReinstall := TNewRadioButton.Create(UpgradePage);
  RadioReinstall.Parent := UpgradePage.Surface;
  RadioReinstall.Top := 0;
  RadioReinstall.Width := ScaleX(350);
  RadioReinstall.Caption := 'Переустановить программу';
  
  RadioUninstall := TNewRadioButton.Create(UpgradePage);
  RadioUninstall.Parent := UpgradePage.Surface;
  RadioUninstall.Top := ScaleY(30);
  RadioUninstall.Width := ScaleX(350);
  RadioUninstall.Caption := 'Удалить программу';
  RadioUninstall.Checked := True;
end;

function InitializeSetup(): Boolean;
begin
  IsUpgrade := CheckIfInstalled();
  Result := True;
end;

procedure InitializeWizard();
begin
  if IsUpgrade then
    CreateUpgradePage();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  
  if IsUpgrade and (CurPageID = UpgradePage.ID) then
  begin
    UninstPath := GetUninstallerPath();
    
    if UninstPath = '' then
    begin
      MsgBox('Не удалось найти деинсталлятор программы.', mbError, MB_OK);
      Result := True;
      Exit;
    end;
    
    if RadioReinstall.Checked then
    begin
      // Переустановка
      if ShellExec('', RemoveQuotes(UninstPath), '/VERYSILENT /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      begin
        Sleep(1500);
        Result := True;
      end
      else
      begin
        MsgBox('Не удалось запустить деинсталлятор.', mbError, MB_OK);
        Result := False;
      end;
    end
    else
    begin
      // Удаление
      ShellExec('', RemoveQuotes(UninstPath), '', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
      WizardForm.Close();
      Result := False;
    end;
  end;
end;

procedure RemovePrinter();
var
  PSCommand: String;
  ResultCode: Integer;
begin
  ShellExec('', 'taskkill.exe', '/F /IM MarklifeWin.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  
  PSCommand := 'Remove-Printer -Name ''X2 Print Label'' -ErrorAction SilentlyContinue; Remove-PrinterPort -Name ''Marklife_127.0.0.1'' -ErrorAction SilentlyContinue; $sizes = @(''20mm x 20mm'',''40mm x 30mm'',''43mm x 25mm'',''50mm x 30mm'',''60mm x 40mm'',''80mm x 60mm'',''100mm x 80mm''); $formsKey = ''HKLM:\SYSTEM\CurrentControlSet\Control\Print\Forms''; foreach ($s in $sizes) { Remove-Item -Path (Join-Path $formsKey $s) -Force -ErrorAction SilentlyContinue; Remove-ItemProperty -Path $formsKey -Name $s -Force -ErrorAction SilentlyContinue }; Restart-Service Spooler -Force -ErrorAction SilentlyContinue';
  
  ShellExec('', 'powershell.exe', '-ExecutionPolicy Bypass -Command "' + PSCommand + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure InstallPrinter();
var
  PSCommand: String;
  ResultCode: Integer;
  TempGpdPath: String;
  SystemRoot: String;
  SpoolPath: String;
begin
  // Извлекаем GPD файл из ресурсов инсталятора во временную папку
  ExtractTemporaryFile('Marklife_X2.gpd');
  TempGpdPath := ExpandConstant('{tmp}\Marklife_X2.gpd');
  SystemRoot := GetEnv('SystemRoot');
  SpoolPath := SystemRoot + '\System32\spool\V4Dirs';

  PSCommand := '$printerName = ''X2 Print Label''; ' +
    'Remove-Printer -Name $printerName -ErrorAction SilentlyContinue; ' +
    'Remove-PrinterPort -Name ''Marklife_127.0.0.1'' -ErrorAction SilentlyContinue; ' +
    '$newPort = ([WMIClass] ''root\cimv2:Win32_TCPIPPrinterPort'').CreateInstance(); ' +
    '$newPort.Name = ''Marklife_127.0.0.1''; ' +
    '$newPort.HostAddress = ''127.0.0.1''; ' +
    '$newPort.PortNumber = 9200; ' +
    '$newPort.Protocol = 1; ' +
    '$newPort.Put(); ' +
    'Add-Printer -Name $printerName -PortName ''Marklife_127.0.0.1'' -DriverName ''Microsoft IPP Class Driver''; ' +
    '$regPath = ''HKLM:\SYSTEM\CurrentControlSet\Control\Print\Printers\'' + $printerName; ' +
    '$v4Dir = Join-Path ''' + SpoolPath + ''' (Get-ItemProperty -Path $regPath -Name ''PrintQueueV4DriverDirectory'' -ErrorAction SilentlyContinue).PrintQueueV4DriverDirectory; ' +
    'if ($v4Dir) { ' +
    'Copy-Item -Path ''' + TempGpdPath + ''' -Destination $v4Dir''\Marklife_X2.gpd'' -Force; ' +
    'Set-ItemProperty -Path $regPath''\PrinterDriverData'' -Name ''V4_Merged_ConfigFile_Name'' -Value ''Marklife_X2.gpd'' -Force ' +
    '}; ' +
    '$sizes = @(@{Name=''20mm x 20mm''; W=20000; H=20000},@{Name=''40mm x 30mm''; W=30000; H=40000},@{Name=''43mm x 25mm''; W=25000; H=43000},@{Name=''50mm x 30mm''; W=30000; H=50000},@{Name=''60mm x 40mm''; W=40000; H=60000},@{Name=''80mm x 60mm''; W=60000; H=80000},@{Name=''100mm x 80mm''; W=80000; H=100000}); ' +
    '$formsKey = ''HKLM:\SYSTEM\CurrentControlSet\Control\Print\Forms''; ' +
    'foreach ($s in $sizes) { ' +
    '$key1 = Join-Path $formsKey $s.Name; ' +
    'New-Item -Path $key1 -Force | Out-Null; ' +
    'Set-ItemProperty -Path $key1 -Name ''FormKeyword'' -Value ([Guid]::NewGuid().ToByteArray()); ' +
    '$bytes = @(); ' +
    '$bytes += [BitConverter]::GetBytes($s.W); ' +
    '$bytes += [BitConverter]::GetBytes($s.H); ' +
    '$bytes += @(0,0,0,0,0,0,0,0); ' +
    '$bytes += [BitConverter]::GetBytes($s.W); ' +
    '$bytes += [BitConverter]::GetBytes($s.H); ' +
    '$bytes += @(0x9d,0x00,0x00,0x00,0x00,0x00,0x00,0x00); ' +
    'Set-ItemProperty -Path $formsKey -Name $s.Name -Value ([byte[]]$bytes) ' +
    '}; ' +
    'Restart-Service Spooler -Force -ErrorAction SilentlyContinue';
  Log(PSCommand);
  ShellExec('', 'powershell.exe', '-ExecutionPolicy Bypass -Command "' + PSCommand + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallPrinter();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemovePrinter();
end;

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: files; Name: "{autodesktop}\Marklife X2 Printer.lnk"