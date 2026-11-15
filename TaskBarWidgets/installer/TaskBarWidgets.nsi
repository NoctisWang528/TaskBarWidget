; -*- coding: utf-8 -*-
; Compile through scripts/package-installer.ps1. No personal runtime directory
; is an input: the generated include contains only verified package files.
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

!ifndef PACKAGE_INCLUDE
  !error "Use package-installer.ps1 to validate and generate the package include."
!endif
!include "${PACKAGE_INCLUDE}"

!define PRODUCT_NAME "TaskBarWidgets"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskBarWidgets-x64"
Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${SETUP_FILE}"
InstallDir "$PROGRAMFILES64\TaskBarWidgets"
RequestExecutionLevel admin
ManifestDPIAware true
CRCCheck force
SetCompressor /SOLID zlib
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${FILE_VERSION}"
VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion" "${FILE_VERSION}"
VIAddVersionKey /LANG=1033 "FileDescription" "TaskBarWidgets Setup"
VIAddVersionKey /LANG=1033 "LegalCopyright" "TaskBarWidgets contributors; see bundled licenses"

!define MUI_ICON "${PRODUCT_ICON}"
!define MUI_UNICON "${PRODUCT_ICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "$(WelcomeText)"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${PACKAGE_LICENSE}"
!insertmacro MUI_PAGE_COMPONENTS
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ValidateInstallDirectory
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "$(FinishText)"
!define MUI_FINISHPAGE_NOAUTOCLOSE
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "$(UninstallFinishText)"
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

LangString WelcomeText ${LANG_SIMPCHINESE} "安装 TaskBarWidgets ${PRODUCT_VERSION}。此安装包仅包含程序，不包含 API 密钥、个人设置或历史记录。$\r$\n$\r$\n需要 Windows 11 x64、.NET 8 Desktop Runtime x64 和 Windows App Runtime 2.5.1 x64（或兼容的更新 2.x）。缺少运行库时，程序会显示下载入口。$\r$\n$\r$\n已有版本请使用程序内更新。安装完成后不会自动启动程序或设置开机启动。"
LangString WelcomeText ${LANG_ENGLISH} "Install TaskBarWidgets ${PRODUCT_VERSION}. This package contains program files only, without API keys, personal settings, or history.$\r$\n$\r$\nRequires Windows 11 x64, .NET 8 Desktop Runtime x64 and Windows App Runtime 2.5.1 x64 (or compatible later 2.x). The app provides download links if a runtime is missing.$\r$\n$\r$\nUse the in-app updater for an existing installation. Setup does not launch the app or enable startup."
LangString FinishText ${LANG_SIMPCHINESE} "安装完成。可从开始菜单打开 TaskBarWidgets。$\r$\n$\r$\n程序运行需要管理员权限。首次启动使用默认设置；API 连接需要你自行配置。"
LangString FinishText ${LANG_ENGLISH} "Installation is complete. Open TaskBarWidgets from the Start menu.$\r$\n$\r$\nThe app requires administrator permission. First launch uses default settings; configure your own API connections as needed."
LangString UninstallFinishText ${LANG_SIMPCHINESE} "程序已卸载。运行后产生的 Data 目录和其中的个人设置、密钥与记录会保留；如不再需要，可自行备份后删除。"
LangString UninstallFinishText ${LANG_ENGLISH} "Program files were removed. The Data directory containing settings, credentials, and history created after installation is preserved. Back it up and remove it manually if no longer needed."
LangString RequiredPlatform ${LANG_SIMPCHINESE} "此安装包仅适用于 Windows 11 x64。"
LangString RequiredPlatform ${LANG_ENGLISH} "This package requires Windows 11 x64."
LangString EmptyDirectoryRequired ${LANG_SIMPCHINESE} "请选择一个空的本地安装文件夹。已有版本请通过程序内更新，避免覆盖个人数据。"
LangString EmptyDirectoryRequired ${LANG_ENGLISH} "Choose an empty local installation folder. Use the in-app updater for an existing installation to preserve personal data."
LangString ExistingInstall ${LANG_SIMPCHINESE} "已检测到通过安装包安装的 TaskBarWidgets。请使用程序内更新，或先卸载旧程序后再安装。"
LangString ExistingInstall ${LANG_ENGLISH} "A setup-managed TaskBarWidgets installation already exists. Use the in-app updater, or uninstall the old program before installing again."
LangString InstallFailed ${LANG_SIMPCHINESE} "无法写入程序文件。安装未完成，请检查目标文件夹的权限和可用空间。"
LangString InstallFailed ${LANG_ENGLISH} "Program files could not be written. Installation is incomplete. Check folder permissions and available space."
LangString CloseBeforeUninstall ${LANG_SIMPCHINESE} "请先退出此目录中的 TaskBarWidgets 和设置窗口，然后重新卸载。不会强制关闭任何程序。"
LangString CloseBeforeUninstall ${LANG_ENGLISH} "Exit TaskBarWidgets and its settings window from this installation, then retry uninstalling. No program will be forcibly closed."
LangString UnsafeUninstall ${LANG_SIMPCHINESE} "安装路径已变化或包含链接，无法安全卸载。未删除程序或个人数据。"
LangString UnsafeUninstall ${LANG_ENGLISH} "The installation path changed or contains a link. Uninstall cannot continue safely. Program and personal data were not removed."
LangString ProgramSection ${LANG_SIMPCHINESE} "程序文件和开始菜单快捷方式（必选）"
LangString ProgramSection ${LANG_ENGLISH} "Program files and Start menu shortcuts (required)"
LangString DesktopSection ${LANG_SIMPCHINESE} "创建桌面快捷方式"
LangString DesktopSection ${LANG_ENGLISH} "Create a desktop shortcut"

; Check every existing ancestor, not just the leaf, before writing or deleting.
; $0 returns 1 for a drive-qualified non-root path without reparse points.
!macro DEFINE_CHECK_DIRECTORY Prefix
Function ${Prefix}CheckDirectoryPath
  StrCpy $0 0
  GetFullPathName $1 "$INSTDIR"
  StrCmp $1 "" directory_done
  StrCpy $2 $1 3
  StrCpy $3 $2 2 1
  StrCmp $3 ":\" 0 directory_done
  StrLen $3 $1
  IntCmp $3 3 directory_done directory_done
  StrCpy $4 $1
directory_ancestor:
  System::Call 'kernel32::GetFileAttributesW(w r4)i.r5'
  IntCmp $5 -1 directory_parent
  IntOp $5 $5 & 0x400
  IntCmp $5 0 directory_parent directory_done directory_done
directory_parent:
  ${GetParent} "$4" $5
  StrCmp $5 "" directory_safe
  StrCmp $5 $4 directory_safe
  StrCpy $4 $5
  Goto directory_ancestor
directory_safe:
  StrCpy $INSTDIR $1
  StrCpy $0 1
directory_done:
FunctionEnd
!macroend
!insertmacro DEFINE_CHECK_DIRECTORY ""
!insertmacro DEFINE_CHECK_DIRECTORY "un."

Function ValidateInstallDirectory
  Call CheckDirectoryPath
  StrCmp $0 1 0 directory_reject
  FindFirst $0 $1 "$INSTDIR\*.*"
directory_next:
  StrCmp $1 "" directory_empty
  StrCmp $1 "." directory_continue
  StrCmp $1 ".." directory_continue
  FindClose $0
  Goto directory_reject
directory_continue:
  FindNext $0 $1
  Goto directory_next
directory_empty:
  FindClose $0
  Return
directory_reject:
  MessageBox MB_ICONSTOP|MB_OK "$(EmptyDirectoryRequired)" /SD IDOK
  SetErrorLevel 2
  Abort
FunctionEnd

Function .onInit
  SetRegView 64
  SetShellVarContext all
  !insertmacro MUI_LANGDLL_DISPLAY
  ${IfNot} ${IsNativeAMD64}
    MessageBox MB_ICONSTOP|MB_OK "$(RequiredPlatform)" /SD IDOK
    SetErrorLevel 2
    Quit
  ${EndIf}
  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  IntCmpU $0 22000 platform_ok platform_reject platform_ok
platform_reject:
  MessageBox MB_ICONSTOP|MB_OK "$(RequiredPlatform)" /SD IDOK
  SetErrorLevel 2
  Quit
platform_ok:
  ReadRegStr $0 HKLM "${UNINSTALL_KEY}" "InstallLocation"
  StrCmp $0 "" init_done
  MessageBox MB_ICONSTOP|MB_OK "$(ExistingInstall)" /SD IDOK
  SetErrorLevel 2
  Quit
init_done:
FunctionEnd

Section "$(ProgramSection)" SEC_PROGRAM
  SectionIn RO
  Call ValidateInstallDirectory
  SetOverwrite off
  ClearErrors
  !insertmacro InstallProgramFiles
  IfErrors install_failed
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  IfErrors install_failed
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "TaskBarWidgets contributors"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\TaskBarWidgets.exe,0"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKLM "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegStr HKLM "${UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/NoctisWang528/TaskBarWidget"
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "EstimatedSize" ${INSTALLED_KB}
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1
  CreateDirectory "$SMPROGRAMS\TaskBarWidgets"
  SetOutPath "$INSTDIR"
  CreateShortcut "$SMPROGRAMS\TaskBarWidgets\TaskBarWidgets.lnk" "$INSTDIR\TaskBarWidgets.exe"
  CreateShortcut "$SMPROGRAMS\TaskBarWidgets\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  Goto install_done
install_failed:
  MessageBox MB_ICONSTOP|MB_OK "$(InstallFailed)" /SD IDOK
  SetErrorLevel 1
  Abort
install_done:
SectionEnd

Section /o "$(DesktopSection)" SEC_DESKTOP
  SetOutPath "$INSTDIR"
  CreateShortcut "$DESKTOP\TaskBarWidgets.lnk" "$INSTDIR\TaskBarWidgets.exe"
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "DesktopShortcut" 1
SectionEnd

; $0 receives the program path; fail before deleting anything if it is in use.
Function un.CheckProgramClosed
  IfFileExists "$0" 0 un_file_closed
  System::Call 'kernel32::CreateFileW(w r0,i 0x80000000,i 0,p 0,i 3,i 0x80,p 0)p.r1'
  IntPtrCmp $1 -1 un_file_open
  System::Call 'kernel32::CloseHandle(p r1)'
un_file_closed:
  Return
un_file_open:
  MessageBox MB_ICONSTOP|MB_OK "$(CloseBeforeUninstall)" /SD IDOK
  SetErrorLevel 2
  Abort
FunctionEnd

; The root check does not cover later links inside Media/UI or other payload
; directories. Check every enumerated directory and file before any deletion.
Function un.CheckPayloadPath
  System::Call 'kernel32::GetFileAttributesW(w r0)i.r1'
  IntCmp $1 -1 un_payload_safe
  IntOp $1 $1 & 0x400
  IntCmp $1 0 un_payload_safe
  MessageBox MB_ICONSTOP|MB_OK "$(UnsafeUninstall)" /SD IDOK
  SetErrorLevel 2
  Abort
un_payload_safe:
FunctionEnd

Function un.ValidateUninstall
  Call un.CheckDirectoryPath
  StrCmp $0 1 0 un_path_reject
  ReadRegStr $0 HKLM "${UNINSTALL_KEY}" "InstallLocation"
  StrCmp $0 $INSTDIR 0 un_path_reject
  !insertmacro CheckUninstallPayload
  StrCpy $0 "$INSTDIR\TaskBarWidgets.exe"
  Call un.CheckProgramClosed
  StrCpy $0 "$INSTDIR\Media\TaskBarWidgets.Media.exe"
  Call un.CheckProgramClosed
  StrCpy $0 "$INSTDIR\UI\TaskBarWidgets.UI.exe"
  Call un.CheckProgramClosed
  StrCpy $0 "$INSTDIR\TaskBarWidgets.PeripheralProbe.exe"
  Call un.CheckProgramClosed
  Return
un_path_reject:
  MessageBox MB_ICONSTOP|MB_OK "$(UnsafeUninstall)" /SD IDOK
  SetErrorLevel 2
  Abort
FunctionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext all
  Call un.ValidateUninstall
FunctionEnd

Section "Uninstall"
  ; No recursive deletion: Data and any other later user-created files survive.
  Call un.ValidateUninstall
  !insertmacro UninstallProgramFiles
  Delete "$SMPROGRAMS\TaskBarWidgets\TaskBarWidgets.lnk"
  Delete "$SMPROGRAMS\TaskBarWidgets\Uninstall.lnk"
  RMDir "$SMPROGRAMS\TaskBarWidgets"
  ReadRegDWORD $0 HKLM "${UNINSTALL_KEY}" "DesktopShortcut"
  IntCmp $0 1 0 no_desktop no_desktop
  Delete "$DESKTOP\TaskBarWidgets.lnk"
no_desktop:
  DeleteRegKey HKLM "${UNINSTALL_KEY}"
  Delete "$INSTDIR\Uninstall.exe"
  SetOutPath "$TEMP"
  RMDir "$INSTDIR"
SectionEnd
