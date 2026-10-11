#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <strsafe.h>
#include <wchar.h>

#ifndef BQTJ_ACCOUNT_EVENT_NAME
#define BQTJ_ACCOUNT_EVENT_NAME L"Local\\BqtjLauncher.AccountManager.Activate"
#endif
/* 小型独立入口复用同目录启动器运行库；不传递凭据，不启动管理面板或游戏。 */
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR command, int show)
{
    wchar_t executable[32768];
    wchar_t *filename;
    DWORD length;
    (void)instance; (void)previous; (void)command; (void)show;
    /* 已打开时仅发送激活事件，不启动第二份 .NET；初次连点由托管互斥锁统一。 */
    HANDLE activation = OpenEventW(EVENT_MODIFY_STATE, FALSE, BQTJ_ACCOUNT_EVENT_NAME);
    if (activation) {
        HWND window = FindWindowW(NULL, L"账号管理工具");
        DWORD process_id = 0;
        if (window) { GetWindowThreadProcessId(window, &process_id); AllowSetForegroundWindow(process_id); }
        SetEvent(activation);
        CloseHandle(activation);
        return 0;
    }    length = GetModuleFileNameW(NULL, executable, ARRAYSIZE(executable));
    if (!length || length >= ARRAYSIZE(executable)) return 1;
    filename = wcsrchr(executable, L'\\');
    if (!filename) return 1;
    filename++;
    if (FAILED(StringCchCopyW(filename, ARRAYSIZE(executable) - (filename - executable), L"BqtjLauncher.Desktop.exe"))) return 1;
    if ((INT_PTR)ShellExecuteW(NULL, L"open", executable, L"--account-tool", NULL, SW_SHOWNORMAL) <= 32) {
        MessageBoxW(NULL, L"无法打开账号工具。请将 BqtjAccountTool.exe 与 BqtjLauncher.Desktop.exe 放在同一目录。",
            L"账号管理工具", MB_OK | MB_ICONERROR);
        return 1;
    }
    return 0;
}
