Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!ifndef VERSION
  !error "VERSION is required"
!endif
!ifndef STAGE
  !error "STAGE is required"
!endif
!ifndef OUTPUT
  !error "OUTPUT is required"
!endif
Name "DualWAN ${VERSION}"
OutFile "${OUTPUT}"
InstallDir "$PROGRAMFILES64\DualWAN"
InstallDirRegKey HKLM "Software\DualWAN" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
Icon "..\DualWAN.Dashboard\Assets\DualWAN.ico"
UninstallIcon "..\DualWAN.Dashboard\Assets\DualWAN.ico"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "DualWAN"
VIAddVersionKey "CompanyName" "DualWAN"
VIAddVersionKey "LegalCopyright" "Copyright DualWAN"
VIAddVersionKey "FileDescription" "DualWAN Installer"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Launch DualWAN"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchDashboard
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Italian"

Function .onInit
  SetRegView 64
FunctionEnd
Function un.onInit
  SetRegView 64
FunctionEnd

Section "DualWAN application and service" SecMain
  SectionIn RO
  SetRegView 64
  SetShellVarContext all
  nsExec::ExecToLog 'taskkill.exe /IM DualWAN.Dashboard.exe /F'
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=WinDivertUpgrade.ps1 "WinDivertUpgrade.ps1"
  File /oname=WinDivert64.sys "${STAGE}\Service\WinDivert64.sys"
  nsExec::ExecToStack 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\WinDivertUpgrade.ps1" -Mode Install -Source "$PLUGINSDIR\WinDivert64.sys" -Destination "$INSTDIR\Service\WinDivert64.sys"'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "DualWAN could not unload or replace the WinDivert network driver. Close applications using WinDivert or restart Windows, then run the installer again.$\r$\n$1"
    Abort
  ${EndIf}
  CreateDirectory "$LOCALAPPDATA\DualWAN\Languages"
  IfFileExists "$INSTDIR\Dashboard\Languages\*.json" 0 +2
    CopyFiles /SILENT "$INSTDIR\Dashboard\Languages\*.json" "$LOCALAPPDATA\DualWAN\Languages"
  RMDir /r "$INSTDIR\Dashboard"
  SetOutPath "$INSTDIR\Service"
  ; The driver was copied with bounded retry before any other installed files change.
  File /r /x "WinDivert64.sys" "${STAGE}\Service\*"
  SetOutPath "$INSTDIR\Dashboard"
  File /r "${STAGE}\Dashboard\*"
  CopyFiles /SILENT "$LOCALAPPDATA\DualWAN\Languages\*.json" "$INSTDIR\Dashboard\Languages"
  ; Current built-in packs replace old built-ins; external packs remain.
  SetOutPath "$INSTDIR\Dashboard\Languages"
  File "${STAGE}\Dashboard\Languages\en-US.json"
  File "${STAGE}\Dashboard\Languages\it-IT.json"
  nsExec::ExecToStack 'sc.exe query DualWANService'
  Pop $0
  Pop $1
  ${If} $0 == 0
    nsExec::ExecToLog 'sc.exe config DualWANService binPath= "$INSTDIR\Service\DualWAN.Service.exe" start= delayed-auto obj= LocalSystem'
  ${Else}
    nsExec::ExecToLog 'sc.exe create DualWANService binPath= "$INSTDIR\Service\DualWAN.Service.exe" start= delayed-auto obj= LocalSystem DisplayName= "DualWAN Routing Service"'
  ${EndIf}
  nsExec::ExecToLog 'sc.exe description DualWANService "Provides per-application routing across multiple WAN interfaces."'
  nsExec::ExecToLog 'sc.exe failure DualWANService reset= 86400 actions= restart/5000/restart/15000/restart/30000'
  nsExec::ExecToLog 'sc.exe failureflag DualWANService 1'
  nsExec::ExecToLog 'sc.exe start DualWANService'
  CreateDirectory "$SMPROGRAMS\DualWAN"
  CreateShortCut "$SMPROGRAMS\DualWAN\DualWAN.lnk" "$INSTDIR\Dashboard\DualWAN.Dashboard.exe" "" "$INSTDIR\Dashboard\Assets\DualWAN.ico"
  CreateShortCut "$SMPROGRAMS\DualWAN\Uninstall DualWAN.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Dashboard\Assets\DualWAN.ico"
  SetRegView 32
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DualWAN"
  DeleteRegKey HKLM "Software\DualWAN"
  SetRegView 64
  WriteRegStr HKLM "Software\DualWAN" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DualWAN" "DisplayName" "DualWAN"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DualWAN" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DualWAN" "Publisher" "DualWAN"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DualWAN" "DisplayIcon" "$INSTDIR\Dashboard\Assets\DualWAN.ico"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DualWAN" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DualWAN" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DualWAN" "NoRepair" 1
  WriteUninstaller "$INSTDIR\Uninstall.exe"
SectionEnd

Section /o "Desktop shortcut" SecDesktop
  SetShellVarContext all
  CreateShortCut "$DESKTOP\DualWAN.lnk" "$INSTDIR\Dashboard\DualWAN.Dashboard.exe" "" "$INSTDIR\Dashboard\Assets\DualWAN.ico"
SectionEnd

Section /o "Start Dashboard with Windows" SecStartup
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "DualWAN" '"$INSTDIR\Dashboard\DualWAN.Dashboard.exe"'
SectionEnd

Function LaunchDashboard
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=LaunchDashboard.ps1 "LaunchDashboard.ps1"
  nsExec::ExecToStack 'powershell.exe -NoProfile -NonInteractive -Sta -ExecutionPolicy Bypass -File "$PLUGINSDIR\LaunchDashboard.ps1" -DashboardPath "$INSTDIR\Dashboard\DualWAN.Dashboard.exe"'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "DualWAN could not be started automatically. You can start it from the Start Menu."
  ${EndIf}
FunctionEnd

Section "Uninstall"
  SetRegView 64
  SetShellVarContext all
  nsExec::ExecToLog 'taskkill.exe /IM DualWAN.Dashboard.exe /F'
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=WinDivertUpgrade.ps1 "WinDivertUpgrade.ps1"
  nsExec::ExecToStack 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\WinDivertUpgrade.ps1" -Mode Uninstall'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "DualWAN could not unload the WinDivert network driver. Close applications using WinDivert or restart Windows, then run the uninstaller again.$\r$\n$1"
    Abort
  ${EndIf}
  nsExec::ExecToLog 'sc.exe delete DualWANService'
  CreateDirectory "$LOCALAPPDATA\DualWAN\Languages"
  IfFileExists "$INSTDIR\Dashboard\Languages\*.json" 0 +2
    CopyFiles /SILENT "$INSTDIR\Dashboard\Languages\*.json" "$LOCALAPPDATA\DualWAN\Languages"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "DualWAN"
  Delete "$DESKTOP\DualWAN.lnk"
  RMDir /r "$SMPROGRAMS\DualWAN"
  RMDir /r "$INSTDIR\Service"
  RMDir /r "$INSTDIR\Dashboard"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DualWAN"
  DeleteRegKey HKLM "Software\DualWAN"
  SetRegView 32
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DualWAN"
  DeleteRegKey HKLM "Software\DualWAN"
  SetRegView 64
  IfSilent done
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "Remove DualWAN configuration and historical data?" IDYES removeData
    Goto done
  removeData:
    RMDir /r "$LOCALAPPDATA\DualWAN"
    SetShellVarContext current
    RMDir /r "$LOCALAPPDATA\DualWAN"
  done:
SectionEnd
