#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

#ifndef PublishDir
  #error PublishDir must point to the application's published files.
#endif

#ifndef InstallerOutputDir
  #define InstallerOutputDir "."
#endif

#define AppName "Disk Usage Analyzer"
#define AppPublisher "Disk Usage Analyzer"
#define AppExeName "DiskUsageAnalyzer.exe"

[Setup]
AppId={{C7B03DCA-1AE9-49B3-B3BC-87812BA9B98C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\Disk Usage Analyzer
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#InstallerOutputDir}
OutputBaseFilename=DiskUsageAnalyzer-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
