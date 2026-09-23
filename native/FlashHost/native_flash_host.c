#define COBJMACROS
#include <windows.h>
#include <wininet.h>
#include <tlhelp32.h>
#include <ole2.h>
#include <oleauto.h>
#include <exdisp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include "virtual_clock.h"
#include "login_autofill.h"

/* 部分 MinGW 头文件缺少 IE8 起的该常量；取微软 WinINet 定义值。 */
#ifndef INTERNET_SUPPRESS_COOKIE_PERSIST
#define INTERNET_SUPPRESS_COOKIE_PERSIST 3
#endif

#define WM_HOST_RESIZE (WM_APP + 1)
#define WM_HOST_RELOAD (WM_APP + 2)
#define WM_HOST_SPEED (WM_APP + 3)
#define WM_HOST_SCALE (WM_APP + 4)
#define WM_HOST_SHOW (WM_APP + 5)
#define WM_HOST_CREDENTIAL (WM_APP + 6)
#define WM_HOST_DISPLAY_READY (WM_APP + 7)

typedef BOOL (WINAPI *atl_ax_win_init_t)(void);
typedef HRESULT (WINAPI *atl_ax_get_control_t)(HWND, IUnknown **);
typedef BOOL (WINAPI *set_process_dpi_awareness_context_t)(HANDLE);
typedef UINT (WINAPI *get_dpi_for_window_t)(HWND);
typedef DWORD (WINAPI *get_tick_count_t)(void);
typedef BOOL (WINAPI *query_performance_counter_t)(LARGE_INTEGER *);

static HWND g_host_window;
static HWND g_browser_window;
static IWebBrowser2 *g_browser;
static atl_ax_get_control_t g_atl_ax_get_control;
static wchar_t g_page_url[2048];
static int g_requested_zoom_percentage = 100;
static unsigned int g_login_attempts;
static int g_applied_zoom_percentage = 100;
static void update_browser_viewport(void);

static void log_hresult(const char *operation, HRESULT result)
{
    fprintf(stderr, "%s failed (HRESULT=0x%08lx)\n", operation, (unsigned long)result);
    fflush(stderr);
}

/*
 * WPF/WinForms 承载进程使用物理像素；原生子进程若保持 DPI Unaware，
 * Windows 会在 150% 显示缩放下把 950×600 二次放大成 1425×900。
 */
static void configure_dpi_awareness(void)
{
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    set_process_dpi_awareness_context_t set_context = user32 == NULL
        ? NULL
        : (set_process_dpi_awareness_context_t)GetProcAddress(
            user32,
            "SetProcessDpiAwarenessContext");
    if (set_context != NULL)
    {
        /* DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 在 SDK 中定义为 -4。 */
        set_context((HANDLE)(intptr_t)-4);
        return;
    }

    SetProcessDPIAware();
}

static HRESULT navigate_page(void)
{
    VARIANT url;
    VARIANT empty;
    VariantInit(&url);
    VariantInit(&empty);
    V_VT(&url) = VT_BSTR;
    V_BSTR(&url) = SysAllocString(g_page_url);
    if (V_BSTR(&url) == NULL)
    {
        return E_OUTOFMEMORY;
    }

    HRESULT result = IWebBrowser2_Navigate2(
        g_browser,
        &url,
        &empty,
        &empty,
        &empty,
        &empty);
    VariantClear(&url);
    return result;
}

/* 重建浏览器控件可清除旧 Flash/IE 实例的加载状态，供“刷新游戏”可靠恢复。 */
static HRESULT recreate_browser(void)
{
    g_login_done = FALSE;
    g_login_attempts = 0;
    g_applied_zoom_percentage = 100;
    if (g_host_window && g_login_username[0]) SetTimer(g_host_window, 91, 500, NULL);
    detach_login_events();
    if (g_browser != NULL)
    {
        IWebBrowser2_Release(g_browser);
        g_browser = NULL;
    }
    if (g_browser_window != NULL)
    {
        ShowWindow(g_browser_window, SW_HIDE);
        DestroyWindow(g_browser_window);
        g_browser_window = NULL;
    }

    RECT bounds;
    GetClientRect(g_host_window, &bounds);
    g_browser_window = CreateWindowExW(
        0,
        L"AtlAxWin",
        L"Shell.Explorer.2",
        /* 浏览器在后台加载；收到 show 命令前，由原生宿主的黑色背景承接画面。 */
        WS_CHILD,
        0,
        0,
        bounds.right,
        bounds.bottom,
        g_host_window,
        NULL,
        GetModuleHandleW(NULL),
        NULL);
    if (g_browser_window == NULL)
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    IUnknown *unknown = NULL;
    HRESULT result = g_atl_ax_get_control(g_browser_window, &unknown);
    if (SUCCEEDED(result))
    {
        result = IUnknown_QueryInterface(unknown, &IID_IWebBrowser2, (void **)&g_browser);
        IUnknown_Release(unknown);
    }
    if (FAILED(result) || g_browser == NULL)
    {
        return FAILED(result) ? result : E_NOINTERFACE;
    }

    IWebBrowser2_put_Silent(g_browser, VARIANT_TRUE);
    attach_login_events(g_browser);
    update_browser_viewport();
    return navigate_page();
}

static void apply_page_zoom(void)
{
    if (g_browser == NULL)
    {
        return;
    }

    VARIANT zoom;
    VariantInit(&zoom);
    V_VT(&zoom) = VT_I4;
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    get_dpi_for_window_t get_dpi_for_window = user32 == NULL
        ? NULL
        : (get_dpi_for_window_t)GetProcAddress(user32, "GetDpiForWindow");
    UINT dpi = get_dpi_for_window == NULL ? 96 : get_dpi_for_window(g_host_window);
    if (dpi == 0)
    {
        dpi = 96;
    }

    /* IE 会按 DPI 缩小旧版 Flash HWND；用等比例光学缩放抵消该虚拟化。 */
    V_I4(&zoom) = (g_requested_zoom_percentage * (int)dpi + 48) / 96;
    if (V_I4(&zoom) == g_applied_zoom_percentage) return;
    HRESULT result = IWebBrowser2_ExecWB(
        g_browser,
        OLECMDID_OPTICAL_ZOOM,
        OLECMDEXECOPT_DONTPROMPTUSER,
        &zoom,
        NULL);
    if (SUCCEEDED(result)) g_applied_zoom_percentage = V_I4(&zoom);
}

/* 原生客户区是唯一布局依据：最大化、全屏、还原和跨 DPI resize 共用这条路径。
 * IE 光学缩放只接受整数百分比，先向下量化再居中，避免四舍五入裁掉游戏边缘。 */
static void update_browser_viewport(void)
{
    if (!g_host_window || !g_browser_window) return;
    RECT bounds; GetClientRect(g_host_window, &bounds);
    int width = bounds.right, height = bounds.bottom;
    if (width <= 0 || height <= 0) return;
    int zoom = min(width * 100 / 950, height * 100 / 600);
    g_requested_zoom_percentage = max(1, min(1000, zoom));
    int game_width = (950 * g_requested_zoom_percentage + 99) / 100;
    int game_height = (600 * g_requested_zoom_percentage + 99) / 100;
    MoveWindow(g_browser_window, (width - game_width) / 2, (height - game_height) / 2,
        game_width, game_height, TRUE);
    apply_page_zoom();
    InvalidateRect(g_host_window, NULL, TRUE);
}

/* 区分页面/Flash 就绪与固定等待：登录页可直接显示，Flash 存在时检查其 ReadyState。
 * 外部资源长期不就绪由 WPF 的有界等待兜底，不无限遮住错误页或验证码。 */
static BOOL is_display_ready(void)
{
    READYSTATE state = READYSTATE_UNINITIALIZED;
    if (!g_browser || FAILED(IWebBrowser2_get_ReadyState(g_browser, &state)) || state < READYSTATE_INTERACTIVE) return FALSE;
    IDispatch *dispatch = NULL, *flash = NULL;
    IHTMLDocument3 *document = NULL;
    IHTMLElement *element = NULL;
    IHTMLObjectElement *object = NULL;
    IWebBrowser2_get_Document(g_browser, &dispatch);
    if (dispatch) IDispatch_QueryInterface(dispatch, &IID_IHTMLDocument3, (void **)&document);
    BSTR id = SysAllocString(L"flashgame");
    if (document) IHTMLDocument3_getElementById(document, id, &element);
    BOOL ready = document && !element;
    if (element) IHTMLElement_QueryInterface(element, &IID_IHTMLObjectElement, (void **)&object);
    if (object) IHTMLObjectElement_get_object(object, &flash);
    if (flash)
    {
        LPOLESTR name = L"ReadyState"; DISPID member;
        VARIANT result; VariantInit(&result); DISPPARAMS arguments = {0};
        if (SUCCEEDED(IDispatch_GetIDsOfNames(flash, &IID_NULL, &name, 1, LOCALE_USER_DEFAULT, &member))
            && SUCCEEDED(IDispatch_Invoke(flash, member, &IID_NULL, LOCALE_USER_DEFAULT,
                DISPATCH_PROPERTYGET, &arguments, &result, NULL, NULL))
            && SUCCEEDED(VariantChangeType(&result, &result, 0, VT_I4))) ready = V_I4(&result) == 4;
        VariantClear(&result);
    }
    SysFreeString(id);
    if (flash) IDispatch_Release(flash);
    if (object) IHTMLObjectElement_Release(object);
    if (element) IHTMLElement_Release(element);
    if (document) IHTMLDocument3_Release(document);
    if (dispatch) IDispatch_Release(dispatch);
    return ready;
}

/* 将 Flash 模块导入的计时 API 改到宿主自有的连续虚拟时钟。 */
static BOOL patch_timer_imports(HMODULE target_module, unsigned int *matched_imports)
{
    if (target_module == NULL)
    {
        return TRUE;
    }

    BYTE *base = (BYTE *)target_module;
    IMAGE_DOS_HEADER *dos_header = (IMAGE_DOS_HEADER *)base;
    if (dos_header->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return FALSE;
    }

    IMAGE_NT_HEADERS *nt_headers = (IMAGE_NT_HEADERS *)(base + dos_header->e_lfanew);
    if (nt_headers->Signature != IMAGE_NT_SIGNATURE)
    {
        return FALSE;
    }

    IMAGE_DATA_DIRECTORY import_directory =
        nt_headers->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (import_directory.VirtualAddress == 0)
    {
        return TRUE;
    }

    IMAGE_IMPORT_DESCRIPTOR *descriptor =
        (IMAGE_IMPORT_DESCRIPTOR *)(base + import_directory.VirtualAddress);
    for (; descriptor->Name != 0; descriptor++)
    {
        if (descriptor->OriginalFirstThunk == 0 || descriptor->FirstThunk == 0)
        {
            continue;
        }

        IMAGE_THUNK_DATA *name_thunk =
            (IMAGE_THUNK_DATA *)(base + descriptor->OriginalFirstThunk);
        IMAGE_THUNK_DATA *address_thunk =
            (IMAGE_THUNK_DATA *)(base + descriptor->FirstThunk);
        for (; name_thunk->u1.AddressOfData != 0; name_thunk++, address_thunk++)
        {
            if (IMAGE_SNAP_BY_ORDINAL(name_thunk->u1.Ordinal))
            {
                continue;
            }

            IMAGE_IMPORT_BY_NAME *import =
                (IMAGE_IMPORT_BY_NAME *)(base + name_thunk->u1.AddressOfData);
            FARPROC replacement = NULL;
            if (strcmp((const char *)import->Name, "GetTickCount") == 0
                || strcmp((const char *)import->Name, "timeGetTime") == 0)
            {
                replacement = (FARPROC)virtual_clock_get_tick_count;
            }
            else if (strcmp((const char *)import->Name, "QueryPerformanceCounter") == 0)
            {
                replacement = (FARPROC)virtual_clock_query_performance_counter;
            }

            if (replacement == NULL)
            {
                continue;
            }

            (*matched_imports)++;
            if (address_thunk->u1.Function == (DWORD_PTR)replacement)
            {
                continue;
            }

            DWORD old_protection;
            if (!VirtualProtect(
                    &address_thunk->u1.Function,
                    sizeof(address_thunk->u1.Function),
                    PAGE_READWRITE,
                    &old_protection))
            {
                return FALSE;
            }

            address_thunk->u1.Function = (DWORD_PTR)replacement;
            DWORD ignored;
            VirtualProtect(
                &address_thunk->u1.Function,
                sizeof(address_thunk->u1.Function),
                old_protection,
                &ignored);
        }
    }

    return TRUE;
}

/* 只改写 Flash ActiveX 本体；改写系统 DLL 会让底层计时调用递归并导致栈溢出。 */
static BOOL is_flash_runtime_module(HMODULE module)
{
    wchar_t path[MAX_PATH];
    DWORD length = GetModuleFileNameW(module, path, ARRAYSIZE(path));
    if (length == 0 || length >= ARRAYSIZE(path))
    {
        return FALSE;
    }

    const wchar_t *file_name = wcsrchr(path, L'\\');
    file_name = file_name == NULL ? path : file_name + 1;
    return _wcsnicmp(file_name, L"Flash", 5) == 0
        && wcsstr(file_name, L".ocx") != NULL;
}

/* Flash ActiveX 会随浏览器页面动态载入，因此每次切换倍率都重新扫描当前模块。 */
static BOOL patch_loaded_timer_imports(void)
{
    HANDLE snapshot = CreateToolhelp32Snapshot(
        TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32,
        GetCurrentProcessId());
    if (snapshot == INVALID_HANDLE_VALUE)
    {
        return FALSE;
    }

    BOOL succeeded = TRUE;
    unsigned int matched_imports = 0;
    MODULEENTRY32W module;
    ZeroMemory(&module, sizeof(module));
    module.dwSize = sizeof(module);
    if (Module32FirstW(snapshot, &module))
    {
        do
        {
            if (is_flash_runtime_module(module.hModule)
                && !patch_timer_imports(module.hModule, &matched_imports))
            {
                succeeded = FALSE;
                break;
            }
        }
        while (Module32NextW(snapshot, &module));
    }
    else
    {
        succeeded = FALSE;
    }

    CloseHandle(snapshot);
    if (succeeded && matched_imports == 0)
    {
        SetLastError(ERROR_PROC_NOT_FOUND);
        return FALSE;
    }

    return succeeded;
}

static BOOL apply_speed(float speed)
{
    if (!virtual_clock_set_speed((double)speed))
    {
        return FALSE;
    }
    return patch_loaded_timer_imports();
}

/*
 * 使用系统管道读取命令，不能用 fgets(stdin)：它在阻塞期间持有 CRT 流锁，
 * IE/Flash 若在 UI 线程调用 fflush(NULL)，就会等输入而冻结整个嵌入窗口。
 * 超长命令整行丢弃，避免截断后的前缀被当成有效控制命令执行。
 */
static BOOL read_command_line(char *line, size_t capacity)
{
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    size_t length = 0;
    BOOL overflow = FALSE;
    char character;
    DWORD received;
    while (ReadFile(input, &character, 1, &received, NULL) && received == 1)
    {
        if (character == '\n')
        {
            if (overflow)
            {
                length = 0;
                overflow = FALSE;
                continue;
            }
            line[length] = '\0';
            return TRUE;
        }
        if (character == '\r') continue;
        if (length + 1 < capacity && !overflow)
            line[length++] = character;
        else
            overflow = TRUE;
    }
    return FALSE;
}

static DWORD WINAPI command_reader(void *unused)
{
    (void)unused;
    char line[8192];
    while (read_command_line(line, sizeof(line)))
    {
        unsigned int width;
        unsigned int height;
        float speed;
        if (strncmp(line, "credential ", 11) == 0)
        {
            /* 当前进程继承的父子管道传输，不使用命令行、环境变量或网页 URL。 */
            SendMessageW(g_host_window, WM_HOST_CREDENTIAL, 0, (LPARAM)(line + 11));
            SecureZeroMemory(line, sizeof(line));
        }
        else if (sscanf(line, "resize %u %u", &width, &height) == 2)
        {
            /* 同步完成尺寸调整，避免 show 命令越过队列而暴露尚未铺满的画面。 */
            SendMessageW(g_host_window, WM_HOST_RESIZE, width, height);
        }
        else if (sscanf(line, "speed %f", &speed) == 1)
        {
            union
            {
                float value;
                LPARAM bits;
            } argument;
            argument.bits = 0;
            argument.value = speed;
            PostMessageW(g_host_window, WM_HOST_SPEED, 0, argument.bits);
        }
        else if (strncmp(line, "reload", 6) == 0)
        {
            /* 同步等待浏览器线程执行 Refresh，让上层能区分成功和空操作。 */
            LRESULT reload_result = SendMessageW(g_host_window, WM_HOST_RELOAD, 0, 0);
            printf(reload_result != 0 ? "reload-ok\n" : "reload-error\n");
            fflush(stdout);
        }
        else if (sscanf(line, "scale %u", &width) == 1)
        {
            SendMessageW(g_host_window, WM_HOST_SCALE, width, 0);
        }
        else if (strcmp(line, "display-ready") == 0)
        {
            LRESULT ready = SendMessageW(g_host_window, WM_HOST_DISPLAY_READY, 0, 0);
            printf(ready ? "display-ready\n" : "display-pending\n");
            fflush(stdout);
        }
        else if (strncmp(line, "show", 4) == 0)
        {
            LRESULT show_result = SendMessageW(g_host_window, WM_HOST_SHOW, 0, 0);
            printf(show_result != 0 ? "show-ok\n" : "show-error\n");
            fflush(stdout);
        }
        else if (strncmp(line, "exit", 4) == 0)
        {
            PostMessageW(g_host_window, WM_CLOSE, 0, 0);
            break;
        }
    }

    PostMessageW(g_host_window, WM_CLOSE, 0, 0);
    return 0;
}

static LRESULT CALLBACK host_window_proc(HWND window, UINT message, WPARAM word, LPARAM value)
{
    switch (message)
    {
        case WM_TIMER:
            if (word == 91 && (++g_login_attempts >= 120 || (g_browser && login_tick(g_browser))))
                KillTimer(window, 91);
            return 0;
        case WM_HOST_CREDENTIAL:
        {
            char *username = (char *)value;
            char *password = strchr(username, ':');
            SecureZeroMemory(g_login_username, sizeof(g_login_username));
            SecureZeroMemory(g_login_password, sizeof(g_login_password));
            if (password)
            {
                *password++ = 0;
                if (!decode_login_hex(username, g_login_username, 257)
                    || !decode_login_hex(password, g_login_password, 1025))
                {
                    SecureZeroMemory(g_login_username, sizeof(g_login_username));
                    SecureZeroMemory(g_login_password, sizeof(g_login_password));
                }
            }
            g_login_done = FALSE;
            g_login_attempts = 0;
            if (g_login_username[0]) SetTimer(window, 91, 500, NULL);
            return 0;
        }
        case WM_SIZE:
            update_browser_viewport();
            return 0;

        case WM_HOST_RESIZE:
            MoveWindow(window, 0, 0, (int)word, (int)value, TRUE);
            return 0;

        case WM_HOST_RELOAD:
            if (g_atl_ax_get_control != NULL)
            {
                HRESULT result = recreate_browser();
                if (FAILED(result))
                {
                    log_hresult("recreate browser for reload", result);
                }
                return SUCCEEDED(result) ? 1 : 0;
            }
            return 0;

        case WM_HOST_SHOW:
            if (g_browser_window != NULL && g_browser != NULL)
            {
                update_browser_viewport();
                ShowWindow(g_browser_window, SW_SHOW);
                UpdateWindow(g_browser_window);
                /* 加载层此时仍隐藏父 HwndHost，IsWindowVisible 会连父窗口一起判断。
                 * 回执只确认子窗口已准备显示，WPF 收到后再揭开父窗口。 */
                return (GetWindowLongPtrW(g_browser_window, GWL_STYLE) & WS_VISIBLE) ? 1 : 0;
            }
            return 0;

        case WM_HOST_DISPLAY_READY:
            return is_display_ready();

        case WM_HOST_SCALE:
            if (word >= 1 && word <= 1000)
            {
                /* 保留旧管道命令兼容性；尺寸档位由 WPF 调整窗口，实际倍率统一按客户区计算。 */
                update_browser_viewport();
            }
            return 0;

        case WM_DPICHANGED:
            /* 仅在系统确认 DPI 变化时重应用光学缩放，避免周期调用触发 Flash 蓝闪。 */
            apply_page_zoom();
            return 0;

#ifdef WM_DPICHANGED_AFTERPARENT
        case WM_DPICHANGED_AFTERPARENT:
            /* 子窗口跟随 WPF 父窗口跨屏后会收到该消息。 */
            apply_page_zoom();
            return 0;
#endif

        case WM_HOST_SPEED:
        {
            union
            {
                float speed;
                LPARAM bits;
            } argument;
            argument.bits = value;
            if (!apply_speed(argument.speed))
            {
                fprintf(stderr, "apply speed failed (win32=%lu)\n", GetLastError());
                printf("speed-error %lu\n", GetLastError());
                fflush(stdout);
            }
            else
            {
                printf("speed-ok\n");
                fflush(stdout);
            }
            return 0;
        }

        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
    }

    return DefWindowProcW(window, message, word, value);
}

static const wchar_t *read_argument(int argc, wchar_t **argv, const wchar_t *name)
{
    for (int index = 1; index < argc - 1; index++)
    {
        if (_wcsicmp(argv[index], name) == 0)
        {
            return argv[index + 1];
        }
    }

    return NULL;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR command_line, int show_command)
{
    (void)previous;
    (void)command_line;
    (void)show_command;
    g_login_auto_submit = TRUE;

    configure_dpi_awareness();

    int argc = 0;
    wchar_t **argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    const wchar_t *parent_text = read_argument(argc, argv, L"--parent");
    const wchar_t *page = read_argument(argc, argv, L"--page");
    if (parent_text == NULL || page == NULL)
    {
        return 2;
    }

    HWND parent = (HWND)(uintptr_t)_wcstoui64(parent_text, NULL, 10);
    if (parent == NULL || !IsWindow(parent))
    {
        return 3;
    }

    wcsncpy(g_page_url, page, ARRAYSIZE(g_page_url) - 1);
    /*
     * 每个账号宿主只使用进程内 Cookie，不继承或覆盖当前用户已保存的登录态。
     * 必须在首次创建 IE/Flash 前设置且仅设置一次；刷新不能再次设置，否则会丢失会话。
     * 初始化失败直接停止，不能退回共享 Cookie 的普通模式。
     */
    DWORD cookie_behavior = INTERNET_SUPPRESS_COOKIE_PERSIST;
    if (!InternetSetOptionW(NULL, INTERNET_OPTION_SUPPRESS_BEHAVIOR,
            &cookie_behavior, sizeof(cookie_behavior)))
    {
        fprintf(stderr, "session cookie isolation initialization failed (win32=%lu)\n", GetLastError());
        fflush(stderr);
        LocalFree(argv);
        return 9;
    }
    HRESULT result = OleInitialize(NULL);
    if (FAILED(result))
    {
        log_hresult("OleInitialize", result);
        return 4;
    }

    HMODULE atl = LoadLibraryW(L"atl.dll");
    atl_ax_win_init_t atl_ax_win_init = atl == NULL
        ? NULL
        : (atl_ax_win_init_t)GetProcAddress(atl, "AtlAxWinInit");
    g_atl_ax_get_control = atl == NULL
        ? NULL
        : (atl_ax_get_control_t)GetProcAddress(atl, "AtlAxGetControl");
    if (atl_ax_win_init == NULL || g_atl_ax_get_control == NULL || !atl_ax_win_init())
    {
        fprintf(stderr, "ATL ActiveX host initialization failed (win32=%lu)\n", GetLastError());
        OleUninitialize();
        return 5;
    }

    WNDCLASSW window_class;
    ZeroMemory(&window_class, sizeof(window_class));
    window_class.lpfnWndProc = host_window_proc;
    window_class.hInstance = instance;
    window_class.lpszClassName = L"BqtjNativeFlashHost";
    window_class.hCursor = LoadCursorW(NULL, IDC_ARROW);
    window_class.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
    if (!RegisterClassW(&window_class))
    {
        return 6;
    }

    RECT bounds;
    GetClientRect(parent, &bounds);
    g_host_window = CreateWindowExW(
        0,
        window_class.lpszClassName,
        L"",
        /* 宿主始终可见并绘制黑底；Flash 浏览器子窗口单独延迟显示。 */
        WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
        0,
        0,
        bounds.right,
        bounds.bottom,
        parent,
        NULL,
        instance,
        NULL);
    if (g_host_window == NULL)
    {
        return 7;
    }

    result = recreate_browser();
    if (FAILED(result))
    {
        log_hresult("create browser and navigate game page", result);
        DestroyWindow(g_host_window);
        return 8;
    }

    printf("ready\n");
    fflush(stdout);

    HANDLE reader = CreateThread(NULL, 0, command_reader, NULL, 0, NULL);
    MSG message;
    while (GetMessageW(&message, NULL, 0, 0) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    if (reader != NULL)
    {
        CancelSynchronousIo(reader);
        WaitForSingleObject(reader, 1000);
        CloseHandle(reader);
    }
    virtual_clock_set_speed(1.0);
    detach_login_events();
    SecureZeroMemory(g_login_username, sizeof(g_login_username));
    SecureZeroMemory(g_login_password, sizeof(g_login_password));
    if (g_browser != NULL)
    {
        IWebBrowser2_Release(g_browser);
    }
    if (g_host_window != NULL && IsWindow(g_host_window))
    {
        DestroyWindow(g_host_window);
    }
    if (atl != NULL)
    {
        FreeLibrary(atl);
    }
    LocalFree(argv);
    OleUninitialize();
    return 0;
}
