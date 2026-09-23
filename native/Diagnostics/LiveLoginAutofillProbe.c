/* 以虚构凭据复现公开登录页的真实 frame 加载链路；绝不提交登录或读取用户数据库。
 * 仅输出来源分类、字段存在性和 HRESULT，不输出 URL 查询串、字段值或页面全文。 */
#include "../FlashHost/native_flash_host.c"

static unsigned g_verified_fields;

static void inspect_document(IHTMLDocument2 *document, unsigned depth)
{
    BSTR url = NULL;
    IHTMLDocument2_get_URL(document, &url);
    BOOL login = login_url_matches(url, L"https://ptlogin.4399.com/ptlogin/loginFrame.do");
    printf("depth=%u login-origin=%d\n", depth, login);
    SysFreeString(url);
    if (login)
    {
        IHTMLDocument3 *doc3 = NULL;
        IHTMLDocument2_QueryInterface(document, &IID_IHTMLDocument3, (void **)&doc3);
        const wchar_t *ids[] = {L"username", L"j-password"};
        for (int i = 0; doc3 && i < 2; i++)
        {
            BSTR id = SysAllocString(ids[i]), value = NULL, action = NULL;
            IHTMLElement *element = NULL;
            IHTMLInputElement *input = NULL;
            IHTMLFormElement *form = NULL;
            HRESULT hr = IHTMLDocument3_getElementById(doc3, id, &element);
            if (element) IHTMLElement_QueryInterface(element, &IID_IHTMLInputElement, (void **)&input);
            if (input) { IHTMLInputElement_get_value(input, &value); IHTMLInputElement_get_form(input, &form); }
            if (form) IHTMLFormElement_get_action(form, &action);
            if (value && wcscmp(value, i == 0 ? g_login_username : g_login_password) == 0)
                g_verified_fields |= 1u << i;
            printf("field=%d hr=%08lx present=%d empty=%d trusted-action=%d relative-action=%d known-hint=%d\n",
                i, (unsigned long)hr, input != NULL, !value || !SysStringLen(value),
                login_action_matches(doc3, L"https://ptlogin.4399.com/ptlogin/loginFrame.do", action),
                action && action[0] == L'/', value && wcscmp(value, L"请输入4399账号或手机号") == 0);
            if (value) SecureZeroMemory(value, SysStringByteLen(value));
            SysFreeString(id); SysFreeString(value); SysFreeString(action);
            if (form) IHTMLFormElement_Release(form);
            if (input) IHTMLInputElement_Release(input);
            if (element) IHTMLElement_Release(element);
        }
        if (doc3) IHTMLDocument3_Release(doc3);
    }
    if (depth >= 4) return;
    IHTMLFramesCollection2 *frames = NULL;
    long count = 0;
    IHTMLDocument2_get_frames(document, &frames);
    if (!frames) return;
    IHTMLFramesCollection2_get_length(frames, &count);
    printf("frames=%ld\n", count);
    for (long i = 0; i < count && i < 16; i++)
    {
        VARIANT index, child; VariantInit(&index); VariantInit(&child);
        V_VT(&index) = VT_I4; V_I4(&index) = i;
        HRESULT hr = IHTMLFramesCollection2_item(frames, &index, &child);
        IHTMLWindow2 *window = NULL; IHTMLDocument2 *doc = NULL;
        if (SUCCEEDED(hr) && V_VT(&child) == VT_DISPATCH)
            hr = IDispatch_QueryInterface(V_DISPATCH(&child), &IID_IHTMLWindow2, (void **)&window);
        if (window) hr = IHTMLWindow2_get_document(window, &doc);
        printf("frame=%ld document-hr=%08lx\n", i, (unsigned long)hr);
        if (doc) { inspect_document(doc, depth + 1); IHTMLDocument2_Release(doc); }
        if (window) IHTMLWindow2_Release(window);
        VariantClear(&child);
    }
    IHTMLFramesCollection2_Release(frames);
}

int main(void)
{
    DWORD behavior = INTERNET_SUPPRESS_COOKIE_PERSIST;
    if (!InternetSetOptionW(NULL, INTERNET_OPTION_SUPPRESS_BEHAVIOR, &behavior, sizeof(behavior))) return 2;
    if (FAILED(OleInitialize(NULL))) return 3;
    HMODULE atl = LoadLibraryW(L"atl.dll");
    atl_ax_win_init_t initialize = atl ? (atl_ax_win_init_t)GetProcAddress(atl, "AtlAxWinInit") : NULL;
    g_atl_ax_get_control = atl ? (atl_ax_get_control_t)GetProcAddress(atl, "AtlAxGetControl") : NULL;
    if (!initialize || !g_atl_ax_get_control || !initialize()) return 4;
    WNDCLASSW type = {0}; type.lpfnWndProc = DefWindowProcW;
    type.hInstance = GetModuleHandleW(NULL); type.lpszClassName = L"BqtjLiveLoginProbe";
    RegisterClassW(&type);
    g_host_window = CreateWindowW(type.lpszClassName, L"", 0, 0, 0, 950, 600, NULL, NULL, type.hInstance, NULL);
    wcscpy(g_page_url, L"https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3680d.htm");
    wcscpy(g_login_username, L"bqtj-synthetic-not-an-account");
    wcscpy(g_login_password, L"synthetic-not-a-password");
    if (FAILED(recreate_browser())) return 5;
    DWORD start = GetTickCount(), last = 0;
    DWORD filled_at = 0;
    /* 填充后继续泵消息五秒，防止只验证写入返回值而漏掉页面脚本随后的清空。 */
    while (GetTickCount() - start < 25000)
    {
        MSG message;
        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
        if (GetTickCount() - last >= 500) { login_tick(g_browser); last = GetTickCount(); }
        if (g_login_done && !filled_at) filled_at = GetTickCount();
        if (filled_at && GetTickCount() - filled_at >= 5000) break;
        Sleep(10);
    }
    IDispatch *dispatch = NULL; IHTMLDocument2 *document = NULL;
    IWebBrowser2_get_Document(g_browser, &dispatch);
    if (dispatch) IDispatch_QueryInterface(dispatch, &IID_IHTMLDocument2, (void **)&document);
    if (document) { inspect_document(document, 0); IHTMLDocument2_Release(document); }
    if (dispatch) IDispatch_Release(dispatch);
    BOOL passed = g_login_done && g_verified_fields == 3;
    printf("live-autofill: %s (both synthetic fields retained=%d)\n", passed ? "PASS" : "FAIL", g_verified_fields == 3); fflush(stdout);
    detach_login_events(); IWebBrowser2_Stop(g_browser); IWebBrowser2_Release(g_browser);
    DestroyWindow(g_host_window); OleUninitialize();
    return passed ? 0 : 1;
}
