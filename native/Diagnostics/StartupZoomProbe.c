/* 离线ATL浏览器回归：实际查询光学倍率，不能只用外层窗口大小判断游戏已缩放。 */
#include "../FlashHost/native_flash_host.c"
#include "flash_render_fixture.h"
#include <psapi.h>

/* 创建文档内 Flash，返回唯一待释放引用；文档/元素等中间 COM 引用不跨刷新保留。 */
static IDispatch *create_flash_fixture(void)
{
    IDispatch *dispatch = NULL, *flash = NULL;
    IHTMLDocument2 *doc = NULL; IHTMLDocument3 *doc3 = NULL;
    IHTMLElement *element = NULL; IHTMLObjectElement *object = NULL;
    IWebBrowser2_get_Document(g_browser, &dispatch);
    if (dispatch) IDispatch_QueryInterface(dispatch, &IID_IHTMLDocument2, (void **)&doc);
    if (!doc) { if (dispatch) IDispatch_Release(dispatch); return NULL; }
    SAFEARRAY *array = SafeArrayCreateVector(VT_VARIANT, 0, 1); VARIANT *value;
    SafeArrayAccessData(array, (void **)&value); VariantInit(value); V_VT(value) = VT_BSTR;
    V_BSTR(value) = SysAllocString(L"<object id='flashgame' classid='clsid:D27CDB6E-AE6D-11cf-96B8-444553540000' width='950' height='600'></object>");
    SafeArrayUnaccessData(array); IHTMLDocument2_write(doc, array); IHTMLDocument2_close(doc); SafeArrayDestroy(array);
    IHTMLDocument2_QueryInterface(doc, &IID_IHTMLDocument3, (void **)&doc3);
    BSTR id = SysAllocString(L"flashgame");
    if (doc3) IHTMLDocument3_getElementById(doc3, id, &element);
    if (element) IHTMLElement_QueryInterface(element, &IID_IHTMLObjectElement, (void **)&object);
    if (object) IHTMLObjectElement_get_object(object, &flash);
    if (object) IHTMLObjectElement_Release(object);
    if (element) IHTMLElement_Release(element);
    if (doc3) IHTMLDocument3_Release(doc3);
    IHTMLDocument2_Release(doc); IDispatch_Release(dispatch); SysFreeString(id);
    return flash;
}

/* 只观察本探针提交量，不读取进程内存内容；有限压力验证不能外推到真实游戏长时在线。 */
static SIZE_T private_bytes(void)
{
    PROCESS_MEMORY_COUNTERS_EX memory = {0}; memory.cb = sizeof(memory);
    GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS *)&memory, sizeof(memory));
    return memory.PrivateUsage;
}

/* 泵送本探针的消息，让 Flash 完成加载与首帧；不驱动其他应用窗口。 */
static void pump_for(DWORD milliseconds)
{
    DWORD start = GetTickCount();
    do {
        MSG message;
        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message); DispatchMessageW(&message);
        }
        Sleep(10);
    } while (GetTickCount() - start < milliseconds);
}

/* 使用自制 SWF，像素断言不受游戏服务器、登录状态或地图内容影响。 */
static BOOL load_render_fixture(IDispatch *flash, wchar_t *path)
{
    GetModuleFileNameW(NULL, path, MAX_PATH);
    wchar_t *filename = wcsrchr(path, L'\\');
    if (!filename) return FALSE;
    wcscpy(filename + 1, L"render-fixture.swf");
    HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) return FALSE;
    DWORD written;
    BOOL saved = WriteFile(file, g_render_fixture, sizeof(g_render_fixture), &written, NULL);
    CloseHandle(file);
    if (!saved || written != sizeof(g_render_fixture)) return FALSE;
    LPOLESTR name = L"Movie"; DISPID member, put = DISPID_PROPERTYPUT;
    VARIANT value; VariantInit(&value); V_VT(&value) = VT_BSTR; V_BSTR(&value) = SysAllocString(path);
    DISPPARAMS args = {&value, &put, 1, 1};
    HRESULT hr = IDispatch_GetIDsOfNames(flash, &IID_NULL, &name, 1, LOCALE_USER_DEFAULT, &member);
    if (SUCCEEDED(hr)) hr = IDispatch_Invoke(flash, member, &IID_NULL, LOCALE_USER_DEFAULT,
        DISPATCH_PROPERTYPUT, &args, NULL, NULL, NULL);
    VariantClear(&value);
    pump_for(500);
    return SUCCEEDED(hr);
}

/* 只枚举本探针的控件窗口，不接触用户已打开的游戏。 */
static BOOL CALLBACK find_flash_window(HWND window, LPARAM state)
{
    wchar_t name[128]; GetClassNameW(window, name, ARRAYSIZE(name));
    if (wcscmp(name, L"MacromediaFlashPlayerActiveX") == 0) {
        *(HWND *)state = window;
        return FALSE;
    }
    return TRUE;
}

/* 捕获 windowed Flash 的实际客户区，测量红矩形跨度，避免 IViewObject 的打印缩放干扰断言。 */
static BOOL check_render(int width, int height)
{
    HWND window = NULL;
    EnumChildWindows(g_host_window, find_flash_window, (LPARAM)&window);
    if (!window) return FALSE;
    HDC dc = CreateCompatibleDC(NULL);
    BITMAPINFO info = {0}; info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = width; info.bmiHeader.biHeight = -height;
    info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32;
    void *pixels;
    HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &pixels, NULL, 0);
    HGDIOBJ previous = SelectObject(dc, bitmap);
    BOOL drawn = PrintWindow(window, dc, PW_CLIENTONLY);
    int red_width = 0, red_height = 0;
    for (int x = 0; x < width; x++) if (GetPixel(dc, x, height / 2) == RGB(255, 0, 0)) red_width++;
    for (int y = 0; y < height; y++) if (GetPixel(dc, width / 2, y) == RGB(255, 0, 0)) red_height++;
    BOOL ok = drawn && abs(red_width - width * 8 / 10) <= 2 && abs(red_height - height * 8 / 10) <= 2;
    printf("render %dx%d: %s (red=%dx%d expected=%dx%d)\n", width, height,
        ok ? "PASS" : "FAIL", red_width, red_height, width * 8 / 10, height * 8 / 10);
    SelectObject(dc, previous); DeleteObject(bitmap); DeleteDC(dc);
    return ok;
}

static BOOL wait_document(void)
{
    DWORD start = GetTickCount();
    while (GetTickCount() - start < 5000)
    {
        MSG message;
        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message); DispatchMessageW(&message);
        }
        READYSTATE state;
        if (SUCCEEDED(IWebBrowser2_get_ReadyState(g_browser, &state)) && state == READYSTATE_COMPLETE) return TRUE;
        Sleep(10);
    }
    return FALSE;
}

/* 操作空白本地夹具的Flash属性，不加载游戏或读取账号。 */
static LONG flash_property(IDispatch *flash, wchar_t *name, BOOL write, LONG value)
{
    DISPID member, put = DISPID_PROPERTYPUT;
    VARIANT argument, result; VariantInit(&argument); VariantInit(&result);
    V_VT(&argument) = VT_I4; V_I4(&argument) = value;
    DISPPARAMS args = {write ? &argument : NULL, write ? &put : NULL, write ? 1 : 0, write ? 1 : 0};
    HRESULT hr = IDispatch_GetIDsOfNames(flash, &IID_NULL, &name, 1, LOCALE_USER_DEFAULT, &member);
    if (SUCCEEDED(hr)) hr = IDispatch_Invoke(flash, member, &IID_NULL, LOCALE_USER_DEFAULT,
        write ? DISPATCH_PROPERTYPUT : DISPATCH_PROPERTYGET, &args, &result, NULL, NULL);
    LONG read = SUCCEEDED(hr) && V_VT(&result) == VT_I4 ? V_I4(&result) : -1;
    VariantClear(&result);
    return write && SUCCEEDED(hr) ? value : read;
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    DWORD behavior = INTERNET_SUPPRESS_COOKIE_PERSIST;
    if (!InternetSetOptionW(NULL, INTERNET_OPTION_SUPPRESS_BEHAVIOR, &behavior, sizeof(behavior))) return 2;
    configure_dpi_awareness();
    if (FAILED(OleInitialize(NULL))) return 2;
    HMODULE atl = LoadLibraryW(L"atl.dll");
    atl_ax_win_init_t initialize = atl ? (atl_ax_win_init_t)GetProcAddress(atl, "AtlAxWinInit") : NULL;
    g_atl_ax_get_control = atl ? (atl_ax_get_control_t)GetProcAddress(atl, "AtlAxGetControl") : NULL;
    if (!initialize || !g_atl_ax_get_control || !initialize()) return 2;
    WNDCLASSW type = {0}; type.lpfnWndProc = host_window_proc;
    type.hInstance = GetModuleHandleW(NULL); type.lpszClassName = L"BqtjStartupZoomProbe";
    RegisterClassW(&type);
    /* Flash 对不可见/屏幕外窗口会延迟绘制；只显示探针自身的夹具，不激活或抢用户焦点。 */
    HWND parent = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, L"STATIC", L"", WS_POPUP | WS_VISIBLE,
        0, 0, 1800, 1200, NULL, NULL, type.hInstance, NULL);
    g_host_window = CreateWindowW(type.lpszClassName, L"", WS_CHILD | WS_VISIBLE, 0, 0, 950, 600, parent, NULL, type.hInstance, NULL);
    wcscpy(g_page_url, L"about:blank");
    if (FAILED(recreate_browser())) return 2;
    if (!wait_document()) return 2;
    IDispatch *flash = create_flash_fixture();
    if (!flash) { puts("Flash fixture unavailable"); return 2; }
    wchar_t fixture_path[MAX_PATH];
    if (!load_render_fixture(flash, fixture_path)) { puts("Render fixture unavailable"); return 2; }
    /* 模拟用户已看到游戏后，再将100%客户区切换到150%。 */
    host_window_proc(g_host_window, WM_HOST_SHOW, 0, 0);
    pump_for(100);
    BOOL original_render = check_render(950, 600);
    flash_property(flash, L"ScaleMode", TRUE, 3);
    flash_property(flash, L"AlignMode", TRUE, 5);
    host_window_proc(g_host_window, WM_HOST_RESIZE, 1425, 900);
    host_window_proc(g_host_window, WM_HOST_SHOW, 0, 0);
    VARIANT actual; VariantInit(&actual);
    HRESULT hr = IWebBrowser2_ExecWB(g_browser, OLECMDID_OPTICAL_ZOOM, OLECMDEXECOPT_DONTPROMPTUSER, NULL, &actual);
    get_dpi_for_window_t read_dpi = (get_dpi_for_window_t)GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetDpiForWindow");
    int expected_zoom = (150 * (int)(read_dpi ? read_dpi(g_host_window) : 96) + 48) / 96;
    BOOL ok = original_render && SUCCEEDED(hr) && V_VT(&actual) == VT_I4 && V_I4(&actual) == expected_zoom
        && g_requested_zoom_percentage == 150;
    LONG scale = flash_property(flash, L"ScaleMode", FALSE, 0);
    LONG align = flash_property(flash, L"AlignMode", FALSE, 0);
    ok = ok && scale == 0 && align == 0;
    printf("flash fit: scale=%ld align=%ld (expected 0/0)\n", scale, align);
    printf("startup150: %s (requested=%d expected=%d actual=%ld hr=%08lx)\n", ok ? "PASS" : "FAIL",
        g_requested_zoom_percentage, expected_zoom, V_VT(&actual) == VT_I4 ? V_I4(&actual) : -1, (unsigned long)hr);
    VariantClear(&actual);
    /* 页面导航或脚本可能重置 IE 倍率；缓存仍为旧值时，同尺寸重新显示也必须恢复画面。 */
    VARIANT reset; VariantInit(&reset); V_VT(&reset) = VT_I4; V_I4(&reset) = 100;
    IWebBrowser2_ExecWB(g_browser, OLECMDID_OPTICAL_ZOOM, OLECMDEXECOPT_DONTPROMPTUSER, &reset, NULL);
    host_window_proc(g_host_window, WM_HOST_SHOW, 0, 0);
    hr = IWebBrowser2_ExecWB(g_browser, OLECMDID_OPTICAL_ZOOM, OLECMDEXECOPT_DONTPROMPTUSER, NULL, &actual);
    BOOL recovered = SUCCEEDED(hr) && V_VT(&actual) == VT_I4 && V_I4(&actual) == expected_zoom;
    printf("zoom reset recovery: %s (expected=%d actual=%ld)\n", recovered ? "PASS" : "FAIL",
        expected_zoom, V_VT(&actual) == VT_I4 ? V_I4(&actual) : -1);
    ok = ok && recovered;
    VariantClear(&actual);
    /* 模拟登录结束后导航完成；没有窗口尺寸变化，也必须恢复新文档的倍率和 Flash Stage。 */
    IWebBrowser2_ExecWB(g_browser, OLECMDID_OPTICAL_ZOOM, OLECMDEXECOPT_DONTPROMPTUSER, &reset, NULL);
    flash_property(flash, L"ScaleMode", TRUE, 3);
    flash_property(flash, L"AlignMode", TRUE, 5);
    g_login_done = TRUE;
    VARIANT event_args[2]; VariantInit(&event_args[0]); VariantInit(&event_args[1]);
    V_VT(&event_args[1]) = VT_DISPATCH; V_DISPATCH(&event_args[1]) = (IDispatch *)g_browser;
    DISPPARAMS args = {event_args, NULL, 2, 0};
    login_invoke(&g_login_sink, 259, &IID_NULL, 0, 0, &args, NULL, NULL, NULL);
    MSG message;
    while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
        TranslateMessage(&message); DispatchMessageW(&message);
    }
    hr = IWebBrowser2_ExecWB(g_browser, OLECMDID_OPTICAL_ZOOM, OLECMDEXECOPT_DONTPROMPTUSER, NULL, &actual);
    BOOL navigation_ok = SUCCEEDED(hr) && V_VT(&actual) == VT_I4 && V_I4(&actual) == expected_zoom
        && flash_property(flash, L"ScaleMode", FALSE, 0) == 0 && flash_property(flash, L"AlignMode", FALSE, 0) == 0;
    printf("document-complete recovery: %s\n", navigation_ok ? "PASS" : "FAIL");
    ok = ok && navigation_ok;
    VariantClear(&actual);
    pump_for(300);
    ok = check_render(1425, 900) && ok;
    /* 覆盖多次放大/还原及非等比容器；Flash 画面应按居中的浏览器矩形等比缩放。 */
    const int layouts[][4] = {{1900, 1200, 1900, 1200}, {1600, 900, 1425, 900}, {950, 600, 950, 600}};
    for (size_t i = 0; i < ARRAYSIZE(layouts); i++) {
        host_window_proc(g_host_window, WM_HOST_RESIZE, layouts[i][0], layouts[i][1]);
        pump_for(150);
        ok = check_render(layouts[i][2], layouts[i][3]) && ok;
    }
    SIZE_T before = private_bytes();
    DWORD handles_before = 0, handles_after = 0;
    GetProcessHandleCount(GetCurrentProcess(), &handles_before);
    /* 反复走生产刷新入口：旧 Flash HWND 必须销毁，新实例仍能加载并画出放大后的内容。 */
    int completed = 0;
    for (; completed < 20; completed++) {
        printf("reload cycle %d\n", completed + 1);
        HWND old_flash = NULL;
        EnumChildWindows(g_host_window, find_flash_window, (LPARAM)&old_flash);
        IDispatch_Release(flash); flash = NULL;
        if (!host_window_proc(g_host_window, WM_HOST_RELOAD, 0, 0) || IsWindow(old_flash) || !wait_document()) {
            ok = FALSE; break;
        }
        flash = create_flash_fixture();
        if (!flash || !load_render_fixture(flash, fixture_path)) { ok = FALSE; break; }
        host_window_proc(g_host_window, WM_HOST_RESIZE, 1425, 900);
        host_window_proc(g_host_window, WM_HOST_SHOW, 0, 0);
        pump_for(100);
        if (!check_render(1425, 900)) { ok = FALSE; break; }
    }
    GetProcessHandleCount(GetCurrentProcess(), &handles_after);
    printf("reload cycles: %s (%d/20 private=%.1f->%.1fMB handles=%lu->%lu)\n", completed == 20 ? "PASS" : "FAIL",
        completed, (double)before / 1048576.0, (double)private_bytes() / 1048576.0, handles_before, handles_after);
    if (flash) IDispatch_Release(flash);
    destroy_browser();
    DeleteFileW(fixture_path);
    DestroyWindow(parent); OleUninitialize();
    return ok ? 0 : 1;
}
