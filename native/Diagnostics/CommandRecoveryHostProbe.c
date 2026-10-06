/* 复用生产命令线程和回执代码，仅用虚构窗口模拟 Flash 主线程短时阻塞。 */
#include "../FlashHost/native_flash_host.c"

static int g_probe_reloads;
static int g_probe_shows;

static LRESULT CALLBACK probe_window_proc(HWND window, UINT message, WPARAM word, LPARAM value)
{
    if (message == WM_HOST_RELOAD)
    {
        /* 首次超过探针请求上限，后续恢复响应；不创建浏览器或加载游戏。 */
        if (++g_probe_reloads == 1) Sleep(1000);
        return 1;
    }
    if (message == WM_HOST_SHOW)
    {
        if (++g_probe_shows == 1)
        {
            puts("stage probe-show-received"); fflush(stdout);
            Sleep(200);
        }
        return 1;
    }
    return host_window_proc(window, message, word, value);
}

int main(void)
{
    WNDCLASSW klass = {0};
    klass.lpfnWndProc = probe_window_proc;
    klass.hInstance = GetModuleHandleW(NULL);
    klass.lpszClassName = L"BqtjSyntheticCommandRecovery";
    if (!RegisterClassW(&klass)) return 2;
    g_host_window = CreateWindowW(klass.lpszClassName, L"", 0, 0, 0, 1, 1,
        NULL, NULL, klass.hInstance, NULL);
    if (!g_host_window) return 2;
    HANDLE reader = CreateThread(NULL, 0, command_reader, NULL, 0, NULL);
    if (!reader) return 2;
    puts("ready"); fflush(stdout);
    MSG message;
    while (GetMessageW(&message, NULL, 0, 0) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    WaitForSingleObject(reader, 1000);
    CloseHandle(reader);
    return 0;
}
