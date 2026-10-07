/* 加载时序回归：自制 SWF 在显示后才运行 Stage.noScale，检查真实像素而非外层窗口。 */
#define main completed_zoom_probe_main
#include "StartupZoomProbe.c"
#undef main

/* 保留原红矩形，在第 13 帧执行 Stage.scaleMode="noScale" 并停止；不访问网络或游戏资源。 */
static BOOL load_delayed_fixture(IDispatch *flash)
{
    BYTE movie[256];
    size_t length = sizeof(g_render_fixture) - 2;
    memcpy(movie, g_render_fixture, length);
    movie[19] = 13; /* 12fps 下先显示约一秒，复现控件已就绪而游戏脚本尚未初始化的阶段。 */
    for (int frame = 1; frame < 12; frame++) {
        movie[length++] = 0x40; movie[length++] = 0; /* ShowFrame */
    }
    static const BYTE action[] = {
        0x96, 7, 0, 0, 'S','t','a','g','e',0, 0x1c, /* Push Stage; GetVariable */
        0x96, 20, 0, 0, 's','c','a','l','e','M','o','d','e',0,
        0, 'n','o','S','c','a','l','e',0, 0x4f, /* Push member/value; SetMember */
        0x07, 0 /* Stop; EndAction */
    };
    WORD tag = (12 << 6) | sizeof(action); /* DoAction */
    movie[length++] = (BYTE)tag; movie[length++] = (BYTE)(tag >> 8);
    memcpy(movie + length, action, sizeof(action)); length += sizeof(action);
    movie[length++] = 0x40; movie[length++] = 0;
    movie[length++] = 0; movie[length++] = 0;
    DWORD file_length = (DWORD)length;
    memcpy(movie + 4, &file_length, sizeof(file_length));
    wchar_t path[MAX_PATH]; GetModuleFileNameW(NULL, path, MAX_PATH);
    wchar_t *filename = wcsrchr(path, L'\\');
    if (!filename) return FALSE;
    wcscpy(filename + 1, L"delayed-scale-fixture.swf");
    HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) return FALSE;
    DWORD written;
    BOOL saved = WriteFile(file, movie, file_length, &written, NULL);
    CloseHandle(file);
    if (!saved || written != file_length) return FALSE;
    LPOLESTR name = L"Movie"; DISPID member, put = DISPID_PROPERTYPUT;
    VARIANT value; VariantInit(&value); V_VT(&value) = VT_BSTR; V_BSTR(&value) = SysAllocString(path);
    DISPPARAMS args = {&value, &put, 1, 1};
    HRESULT hr = IDispatch_GetIDsOfNames(flash, &IID_NULL, &name, 1, LOCALE_USER_DEFAULT, &member);
    if (SUCCEEDED(hr)) hr = IDispatch_Invoke(flash, member, &IID_NULL, LOCALE_USER_DEFAULT,
        DISPATCH_PROPERTYPUT, &args, NULL, NULL, NULL);
    VariantClear(&value);
    /* Flash 加载自身文件后不再需要它；这里只泵送首帧，不等待延迟脚本。 */
    pump_for(100);
    DeleteFileW(path);
    return SUCCEEDED(hr);
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
    type.hInstance = GetModuleHandleW(NULL); type.lpszClassName = L"BqtjLoadingZoomProbe";
    RegisterClassW(&type);
    /* 仅显示探针自己的动画窗口，既不激活也不调整保留现场的游戏。 */
    HWND parent = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, L"STATIC", L"", WS_POPUP | WS_VISIBLE,
        0, 0, 1800, 1200, NULL, NULL, type.hInstance, NULL);
    g_host_window = CreateWindowW(type.lpszClassName, L"", WS_CHILD | WS_VISIBLE, 0, 0, 950, 600,
        parent, NULL, type.hInstance, NULL);
    wcscpy(g_page_url, L"about:blank");
    BOOL ok = TRUE;
    /* 三轮分别覆盖加载中切档、控件尚未创建时切档，以及刷新后连续放大/还原。 */
    for (int variant = 0; variant < 3; variant++) {
        host_window_proc(g_host_window, WM_HOST_RESIZE, 950, 600);
        if (!SUCCEEDED(recreate_browser()) || !wait_document()) { ok = FALSE; break; }
        if (variant == 1) host_window_proc(g_host_window, WM_HOST_RESIZE, 1425, 900);
        IDispatch *flash = create_flash_fixture();
        if (!flash || !load_delayed_fixture(flash)) {
            if (flash) IDispatch_Release(flash);
            ok = FALSE; break;
        }
        if (variant != 1) host_window_proc(g_host_window, WM_HOST_RESIZE, 1425, 900);
        host_window_proc(g_host_window, WM_HOST_SHOW, 0, 0);
        if (variant == 2) {
            host_window_proc(g_host_window, WM_HOST_RESIZE, 1900, 1200);
            host_window_proc(g_host_window, WM_HOST_RESIZE, 950, 600);
            host_window_proc(g_host_window, WM_HOST_RESIZE, 1425, 900);
        }
        printf("loading case=%d ready=%d scale=%ld\n", variant, is_display_ready(), flash_property(flash, L"ScaleMode", FALSE, 0));
        pump_for(200);
        ok = check_render(1425, 900) && ok;
        pump_for(1800);
        printf("after initialization ready=%d scale=%ld\n", is_display_ready(), flash_property(flash, L"ScaleMode", FALSE, 0));
        ok = check_render(1425, 900) && ok;
        IDispatch_Release(flash);
    }
    destroy_browser(); DestroyWindow(parent); OleUninitialize();
    return ok ? 0 : 1;
}
