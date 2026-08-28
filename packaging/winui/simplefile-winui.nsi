Unicode True
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"

!ifndef PAYLOAD
  !error "PAYLOAD must be defined (staged WinUI payload directory)."
!endif
!ifndef VERSION
  !error "VERSION must be defined."
!endif
!ifndef OUTFILE
  !error "OUTFILE must be defined."
!endif
!ifndef ICON
  !define ICON "icon.ico"
!endif

Name "SumaFile"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\SumaFile-WinUI"
RequestExecutionLevel user
SetCompressor /SOLID lzma
Icon "${ICON}"
UninstallIcon "${ICON}"
BrandingText "SumaFile ${VERSION}"

!define MUI_ABORTWARNING
!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"

!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "SumaFile"
VIAddVersionKey /LANG=1033 "FileDescription" "SumaFile WinUI host"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "CompanyName" "SumaFile Team"
VIAddVersionKey /LANG=1033 "LegalCopyright" "SumaFile Team"

Function .onInit
  nsExec::ExecToLog 'taskkill /F /IM SumaFile.exe'
  nsExec::ExecToLog 'taskkill /F /IM SimpleFile.exe'
  nsExec::ExecToLog 'taskkill /F /IM SimpleFile.App.exe'
  nsExec::ExecToLog 'taskkill /F /IM simplefile-service.exe'
  nsExec::ExecToLog 'taskkill /F /IM simplefile.exe'
  Pop $0
FunctionEnd

Section "Install"
  SetOutPath "$INSTDIR"
  File /r "${PAYLOAD}\*.*"

  CreateDirectory "$SMPROGRAMS"
  Delete "$SMPROGRAMS\SimpleFile (WinUI).lnk"
  Delete "$DESKTOP\SimpleFile (WinUI).lnk"
  CreateShortCut "$SMPROGRAMS\SumaFile.lnk" "$INSTDIR\SumaFile.exe" "" "$INSTDIR\SumaFile.exe" 0
  CreateShortCut "$DESKTOP\SumaFile.lnk" "$INSTDIR\SumaFile.exe" "" "$INSTDIR\SumaFile.exe" 0

  WriteUninstaller "$INSTDIR\uninstall.exe"

  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI" "DisplayName" "SumaFile"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI" "Publisher" "SumaFile Team"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI" "DisplayIcon" "$INSTDIR\SumaFile.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI" "UninstallString" "$\"$INSTDIR\uninstall.exe$\""
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI" "QuietUninstallString" "$\"$INSTDIR\uninstall.exe$\" /S"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  nsExec::ExecToLog 'taskkill /F /IM SumaFile.exe'
  nsExec::ExecToLog 'taskkill /F /IM SimpleFile.exe'
  nsExec::ExecToLog 'taskkill /F /IM simplefile-service.exe'
  Pop $0

  Delete "$SMPROGRAMS\SumaFile.lnk"
  Delete "$DESKTOP\SumaFile.lnk"
  Delete "$SMPROGRAMS\SimpleFile (WinUI).lnk"
  Delete "$DESKTOP\SimpleFile (WinUI).lnk"
  Delete "$INSTDIR\uninstall.exe"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI"
SectionEnd
