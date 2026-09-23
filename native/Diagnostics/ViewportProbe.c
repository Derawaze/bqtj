/* 直接驱动生产 resize/scale 消息，只创建本进程隐藏夹具窗口，不访问用户桌面或网络。 */
#include "../FlashHost/native_flash_host.c"

static int check_layout(int width, int height, int zoom, int left, int top, int game_width, int game_height)
{
    host_window_proc(g_host_window, WM_HOST_RESIZE, width, height);
    RECT bounds; GetWindowRect(g_browser_window, &bounds);
    MapWindowPoints(NULL, g_host_window, (POINT *)&bounds, 2);
    BOOL ok = g_requested_zoom_percentage == zoom && bounds.left == left && bounds.top == top
        && bounds.right - bounds.left == game_width && bounds.bottom - bounds.top == game_height;
    printf("viewport %dx%d: %s (zoom=%d rect=%ld,%ld,%ld,%ld)\n", width, height,
        ok ? "PASS" : "FAIL", g_requested_zoom_percentage, bounds.left, bounds.top, bounds.right, bounds.bottom);
    return ok ? 0 : 1;
}

int main(void)
{
    WNDCLASSW type = {0}; type.lpfnWndProc = host_window_proc;
    type.hInstance = GetModuleHandleW(NULL); type.lpszClassName = L"BqtjViewportProbe";
    RegisterClassW(&type);
    HWND parent = CreateWindowW(L"STATIC", L"", WS_POPUP, 0, 0, 2200, 1800, NULL, NULL, type.hInstance, NULL);
    g_host_window = CreateWindowW(type.lpszClassName, L"", WS_CHILD, 0, 0, 950, 600, parent, NULL, type.hInstance, NULL);
    g_browser_window = CreateWindowW(L"STATIC", L"", WS_CHILD, 0, 0, 950, 600, g_host_window, NULL, type.hInstance, NULL);
    int failures = 0;
    failures += check_layout(1600, 900, 150, 87, 0, 1425, 900);
    failures += check_layout(950, 600, 100, 0, 0, 950, 600);
    failures += check_layout(1000, 1200, 105, 1, 285, 998, 630);
    failures += check_layout(1920, 1038, 173, 138, 0, 1644, 1038);
    DestroyWindow(parent);
    return failures ? 1 : 0;
}
