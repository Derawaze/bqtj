/* 复用真实管道读取和窗口消息处理，验证编码凭据确实到达浏览器 UI 线程。 */
#include "../FlashHost/native_flash_host.c"

static LRESULT CALLBACK probe_proc(HWND window, UINT message, WPARAM word, LPARAM value)
{
    if (message == WM_HOST_CREDENTIAL)
    {
        host_window_proc(window, message, word, value);
        BOOL ok = wcscmp(g_login_username, L"synthetic-A") == 0
            && wcscmp(g_login_password, L"test:\n\"password") == 0;
        puts(ok ? "credential-pipe: PASS" : "credential-pipe: FAIL");
        fflush(stdout);
        PostQuitMessage(ok ? 0 : 1);
        return 0;
    }
    if (message == WM_TIMER) { PostQuitMessage(2); return 0; }
    return DefWindowProcW(window, message, word, value);
}

int main(void)
{
    WNDCLASSW type = {0};
    type.lpfnWndProc = probe_proc;
    type.hInstance = GetModuleHandleW(NULL);
    type.lpszClassName = L"BqtjCredentialPipeProbe";
    RegisterClassW(&type);
    g_host_window = CreateWindowW(type.lpszClassName, L"", 0, 0, 0, 1, 1, HWND_MESSAGE, NULL, type.hInstance, NULL);
    if (!g_host_window) return 3;
    SetTimer(g_host_window, 1, 3000, NULL);
    HANDLE reader = CreateThread(NULL, 0, command_reader, NULL, 0, NULL);
    MSG message;
    while (GetMessageW(&message, NULL, 0, 0) > 0) DispatchMessageW(&message);
    if (reader) { CancelSynchronousIo(reader); CloseHandle(reader); }
    ExitProcess((UINT)message.wParam);
}
