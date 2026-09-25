/* 离线ATL浏览器回归：实际查询光学倍率，不能只用外层窗口大小判断游戏已缩放。 */
#include "../FlashHost/native_flash_host.c"

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
    HWND parent = CreateWindowW(L"STATIC", L"", WS_POPUP, 0, 0, 1800, 1200, NULL, NULL, type.hInstance, NULL);
    g_host_window = CreateWindowW(type.lpszClassName, L"", WS_CHILD, 0, 0, 950, 600, parent, NULL, type.hInstance, NULL);
    wcscpy(g_page_url, L"about:blank");
    if (FAILED(recreate_browser())) return 2;
    if (!wait_document()) return 2;
    IDispatch *dispatch = NULL, *flash = NULL;
    IHTMLDocument2 *doc = NULL; IHTMLDocument3 *doc3 = NULL;
    IHTMLElement *element = NULL; IHTMLObjectElement *object = NULL;
    IWebBrowser2_get_Document(g_browser, &dispatch);
    if (dispatch) IDispatch_QueryInterface(dispatch, &IID_IHTMLDocument2, (void **)&doc);
    if (!doc) return 2;
    SAFEARRAY *array = SafeArrayCreateVector(VT_VARIANT, 0, 1); VARIANT *value;
    SafeArrayAccessData(array, (void **)&value); VariantInit(value); V_VT(value) = VT_BSTR;
    V_BSTR(value) = SysAllocString(L"<object id='flashgame' classid='clsid:D27CDB6E-AE6D-11cf-96B8-444553540000' width='950' height='600'></object>");
    SafeArrayUnaccessData(array); IHTMLDocument2_write(doc, array); IHTMLDocument2_close(doc); SafeArrayDestroy(array);
    IHTMLDocument2_QueryInterface(doc, &IID_IHTMLDocument3, (void **)&doc3);
    BSTR id = SysAllocString(L"flashgame");
    if (doc3) IHTMLDocument3_getElementById(doc3, id, &element);
    if (element) IHTMLElement_QueryInterface(element, &IID_IHTMLObjectElement, (void **)&object);
    if (object) IHTMLObjectElement_get_object(object, &flash);
    if (!flash) { puts("Flash fixture unavailable"); return 2; }
    /* 模拟用户已看到游戏后，再将100%客户区切换到150%。 */
    host_window_proc(g_host_window, WM_HOST_SHOW, 0, 0);
    flash_property(flash, L"ScaleMode", TRUE, 3);
    flash_property(flash, L"AlignMode", TRUE, 5);
    host_window_proc(g_host_window, WM_HOST_RESIZE, 1425, 900);
    host_window_proc(g_host_window, WM_HOST_SHOW, 0, 0);
    VARIANT actual; VariantInit(&actual);
    HRESULT hr = IWebBrowser2_ExecWB(g_browser, OLECMDID_OPTICAL_ZOOM, OLECMDEXECOPT_DONTPROMPTUSER, NULL, &actual);
    BOOL ok = SUCCEEDED(hr) && V_VT(&actual) == VT_I4 && V_I4(&actual) == g_applied_zoom_percentage
        && g_requested_zoom_percentage == 150;
    LONG scale = flash_property(flash, L"ScaleMode", FALSE, 0);
    LONG align = flash_property(flash, L"AlignMode", FALSE, 0);
    ok = ok && scale == 0 && align == 0;
    printf("flash fit: scale=%ld align=%ld (expected 0/0)\n", scale, align);
    printf("startup150: %s (requested=%d cached=%d actual=%ld hr=%08lx)\n", ok ? "PASS" : "FAIL",
        g_requested_zoom_percentage, g_applied_zoom_percentage, V_VT(&actual) == VT_I4 ? V_I4(&actual) : -1, (unsigned long)hr);
    VariantClear(&actual);
    IDispatch_Release(flash); IHTMLObjectElement_Release(object); IHTMLElement_Release(element);
    IHTMLDocument3_Release(doc3); IHTMLDocument2_Release(doc); IDispatch_Release(dispatch); SysFreeString(id);
    detach_login_events(); IWebBrowser2_Stop(g_browser); IWebBrowser2_Release(g_browser); g_browser = NULL;
    DestroyWindow(parent); OleUninitialize();
    return ok ? 0 : 1;
}
