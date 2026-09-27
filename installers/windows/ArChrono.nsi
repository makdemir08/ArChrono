; ArChrono Windows kurulum sihirbazı (NSIS 3).
; Doğrudan çağırmayın; scripts/package-windows.sh şu tanımlarla derler:
;   -DVERSION=0.1.0 -DARCH=win-x64|win-arm64 -DSOURCE_DIR=<ArChrono.exe klasörü> -DICON=<ico> -DOUTFILE=<çıktı>
; Kullanıcı başına kurulum yapar (yönetici izni gerekmez): %LOCALAPPDATA%\Programs\ArChrono

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

!define APP_NAME "ArChrono"
!define APP_EXE "ArChrono.exe"
!define PUBLISHER "ArSoft"
!define WEBSITE "https://www.arsoft.com.tr"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"

Name "${APP_NAME} ${VERSION}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "${UNINST_KEY}" "InstallLocation"
RequestExecutionLevel user
BrandingText "${APP_NAME} ${VERSION} · ${PUBLISHER}"

!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; Sihirbaz dili Windows diline göre seçilir (uygulamanın kendisi gibi Türkçe / İngilizce).
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Turkish"

LangString MsgNeedsWin10 ${LANG_ENGLISH} "${APP_NAME} requires Windows 10 or later."
LangString MsgNeedsWin10 ${LANG_TURKISH} "${APP_NAME} Windows 10 veya üzeri gerektirir."
LangString MsgNeedsArm64 ${LANG_ENGLISH} "This installer is for Windows on ARM64 processors. Please download the x64 version."
LangString MsgNeedsArm64 ${LANG_TURKISH} "Bu kurulum ARM64 işlemcili Windows içindir. Lütfen x64 sürümünü indirin."
LangString MsgNeeds64Bit ${LANG_ENGLISH} "${APP_NAME} requires 64-bit Windows."
LangString MsgNeeds64Bit ${LANG_TURKISH} "${APP_NAME} 64-bit Windows gerektirir."
LangString MsgGitMissing ${LANG_ENGLISH} "Git was not found on this computer. ${APP_NAME} uses the Git you install yourself.$\r$\n$\r$\nInstall Git for Windows 2.38 or newer from git-scm.com before using ${APP_NAME}."
LangString MsgGitMissing ${LANG_TURKISH} "Bu bilgisayarda Git bulunamadı. ${APP_NAME} sizin kurduğunuz Git'i kullanır.$\r$\n$\r$\n${APP_NAME}'yu kullanmadan önce git-scm.com adresinden Git for Windows 2.38 veya üzerini kurun."
LangString SecMainName ${LANG_ENGLISH} "${APP_NAME} (required)"
LangString SecMainName ${LANG_TURKISH} "${APP_NAME} (gerekli)"
LangString SecDesktopName ${LANG_ENGLISH} "Desktop shortcut"
LangString SecDesktopName ${LANG_TURKISH} "Masaüstü kısayolu"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=${LANG_ENGLISH} "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "FileDescription" "${APP_NAME} Setup"
VIAddVersionKey /LANG=${LANG_ENGLISH} "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "LegalCopyright" "© ${PUBLISHER}"

Function .onInit
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "$(MsgNeedsWin10)"
    Abort
  ${EndIf}
!if "${ARCH}" == "win-arm64"
  ${IfNot} ${IsNativeARM64}
    MessageBox MB_ICONSTOP "$(MsgNeedsArm64)"
    Abort
  ${EndIf}
!else
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "$(MsgNeeds64Bit)"
    Abort
  ${EndIf}
!endif
FunctionEnd

Section "$(SecMainName)" SecMain
  SectionIn RO

  ; Güncelleme sırasında çalışan örneği kapat (dosya kilidini önler).
  nsExec::Exec 'taskkill /IM ${APP_EXE} /F'
  Pop $0

  SetOutPath "$INSTDIR"
  File "${SOURCE_DIR}\${APP_EXE}"
  File "/oname=ArChrono.ico" "${ICON}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateShortcut "$SMPROGRAMS\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\ArChrono.ico" 0

  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "URLInfoAbout" "${WEBSITE}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\ArChrono.ico"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" $0

  ; ArChrono sistemdeki Git'i kullanır; yoksa kurulum engellenmez, yalnızca uyarılır.
  SearchPath $1 "git.exe"
  ${If} $1 == ""
  ${AndIfNot} ${FileExists} "$PROGRAMFILES64\Git\cmd\git.exe"
  ${AndIfNot} ${FileExists} "$LOCALAPPDATA\Programs\Git\cmd\git.exe"
    MessageBox MB_ICONINFORMATION "$(MsgGitMissing)" /SD IDOK
  ${EndIf}
SectionEnd

Section "$(SecDesktopName)" SecDesktop
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\ArChrono.ico" 0
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /IM ${APP_EXE} /F'
  Pop $0

  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\ArChrono.ico"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  Delete "$SMPROGRAMS\${APP_NAME}.lnk"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  DeleteRegKey HKCU "${UNINST_KEY}"
  ; Recovery point'ler, snapshot'lar, ayarlar ve loglar (%LOCALAPPDATA%\ArChrono) korunur.
SectionEnd
