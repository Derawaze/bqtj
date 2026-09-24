#define COBJMACROS
#include <initguid.h>
#include <windows.h>
#include <ole2.h>
#include <exdisp.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include "../FlashHost/login_autofill.h"

/* 离线创建真实 MSHTML 文档，不访问登录站点、不包含或提交真实凭据。 */
static IHTMLDocument2 *fixture(const wchar_t *html)
{
    IHTMLDocument2 *document = NULL;
    if (FAILED(CoCreateInstance(&CLSID_HTMLDocument, NULL, CLSCTX_INPROC_SERVER,
        &IID_IHTMLDocument2, (void **)&document))) return NULL;
    SAFEARRAY *array = SafeArrayCreateVector(VT_VARIANT, 0, 1);
    VARIANT *value;
    SafeArrayAccessData(array, (void **)&value);
    VariantInit(value); V_VT(value) = VT_BSTR; V_BSTR(value) = SysAllocString(html);
    SafeArrayUnaccessData(array);
    IHTMLDocument2_write(document, array);
    IHTMLDocument2_close(document);
    SafeArrayDestroy(array);
    return document;
}
static BOOL has_value(IHTMLDocument2 *document, const wchar_t *id, const wchar_t *expected)
{
    IHTMLDocument3 *doc3 = NULL;
    IHTMLElement *element = NULL;
    IHTMLInputElement *input = NULL;
    BSTR name = SysAllocString(id), value = NULL;
    IHTMLDocument2_QueryInterface(document, &IID_IHTMLDocument3, (void **)&doc3);
    if (doc3) IHTMLDocument3_getElementById(doc3, name, &element);
    if (element) IHTMLElement_QueryInterface(element, &IID_IHTMLInputElement, (void **)&input);
    if (input) IHTMLInputElement_get_value(input, &value);
    BOOL ok = value && wcscmp(value, expected) == 0;
    SysFreeString(name); SysFreeString(value);
    if (input) IHTMLInputElement_Release(input);
    if (element) IHTMLElement_Release(element);
    if (doc3) IHTMLDocument3_Release(doc3);
    return ok;
}

static unsigned submit_clicks;
/* 在真实 DOM 的 onclick 上截获测试提交并取消默认行为，夹具绝不发出网络请求。 */
static HRESULT STDMETHODCALLTYPE count_submit(IDispatch *self, DISPID id, REFIID iid, LCID locale, WORD flags,
    DISPPARAMS *args, VARIANT *result, EXCEPINFO *exception, UINT *error)
{
    (void)self; (void)id; (void)iid; (void)locale; (void)flags; (void)args; (void)exception; (void)error;
    submit_clicks++;
    if (result) { V_VT(result) = VT_BOOL; V_BOOL(result) = VARIANT_FALSE; }
    return S_OK;
}
static IDispatchVtbl submit_vtable = { login_query, login_addref, login_release, login_type_count, login_type, login_names, count_submit };
static IDispatch submit_sink = { &submit_vtable };

/* onclick 需要激活的浏览器文档；独立 HTMLDocument 虽可改 DOM，却不派发真实点击事件。 */
static int verify_submit(const wchar_t *html, unsigned expected_clicks)
{
    HMODULE atl = LoadLibraryW(L"atl.dll");
    BOOL (WINAPI *initialize)(void) = (void *)GetProcAddress(atl, "AtlAxWinInit");
    HRESULT (WINAPI *get_control)(HWND, IUnknown **) = (void *)GetProcAddress(atl, "AtlAxGetControl");
    if (!initialize || !get_control || !initialize()) return 1;
    HWND host = CreateWindowW(L"AtlAxWin", L"about:blank", WS_POPUP, 0, 0, 950, 600, NULL, NULL, GetModuleHandleW(NULL), NULL);
    IUnknown *control = NULL; IWebBrowser2 *browser = NULL; IDispatch *dispatch = NULL;
    get_control(host, &control);
    if (control) IUnknown_QueryInterface(control, &IID_IWebBrowser2, (void **)&browser);
    if (browser) IWebBrowser2_get_Document(browser, &dispatch);
    IHTMLDocument2 *document = NULL;
    if (dispatch) IDispatch_QueryInterface(dispatch, &IID_IHTMLDocument2, (void **)&document);
    if (dispatch) IDispatch_Release(dispatch);
    if (browser) IWebBrowser2_Release(browser);
    if (control) IUnknown_Release(control);
    if (document)
    {
        SAFEARRAY *array = SafeArrayCreateVector(VT_VARIANT, 0, 1);
        VARIANT *item; SafeArrayAccessData(array, (void **)&item);
        VariantInit(item); V_VT(item) = VT_BSTR; V_BSTR(item) = SysAllocString(html);
        SafeArrayUnaccessData(array); IHTMLDocument2_write(document, array);
        IHTMLDocument2_close(document); SafeArrayDestroy(array);
    }
    IHTMLDocument3 *doc3 = NULL; IHTMLElement *button = NULL; IHTMLElement2 *button2 = NULL;
    BSTR id = SysAllocString(L"j-login-submit-btn"), event = SysAllocString(L"onclick"), url = NULL;
    if (!document) return 1;
    IHTMLDocument2_QueryInterface(document, &IID_IHTMLDocument3, (void **)&doc3);
    if (doc3) IHTMLDocument3_getElementById(doc3, id, &button);
    if (button) IHTMLElement_QueryInterface(button, &IID_IHTMLElement2, (void **)&button2);
    VARIANT_BOOL attached = VARIANT_FALSE;
    if (button2) IHTMLElement2_attachEvent(button2, event, &submit_sink, &attached);
    IHTMLDocument2_get_URL(document, &url);
    submit_clicks = 0; g_login_done = FALSE; g_login_auto_submit = TRUE;
    /* 离线文档完成解析后与生产入口相同，第二次调用不得再次提交已填写字段。 */
    DWORD start = GetTickCount();
    while (GetTickCount() - start < 1000 && !g_login_done)
    {
        MSG message;
        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
        fill_login_document_at_url(document, url);
        Sleep(10);
    }
    fill_login_document_at_url(document, url);
    int failed = !attached || submit_clicks != expected_clicks;
    if (failed)
    {
        BSTR ready = NULL; IHTMLDocument2_get_readyState(document, &ready);
        printf("fixture diagnostic: ready=%ls attached=%d filled=%d\n", ready ? ready : L"null", attached, g_login_done);
        SysFreeString(ready);
    }
    printf("submit fixture: %s (clicks=%u expected=%u)\n", failed ? "FAIL" : "PASS", submit_clicks, expected_clicks);
    g_login_auto_submit = FALSE; g_login_done = FALSE;
    if (button2) { IHTMLElement2_detachEvent(button2, event, &submit_sink); IHTMLElement2_Release(button2); }
    if (button) IHTMLElement_Release(button);
    if (doc3) IHTMLDocument3_Release(doc3);
    IHTMLDocument2_Release(document); SysFreeString(id); SysFreeString(event); SysFreeString(url);
    DestroyWindow(host); FreeLibrary(atl);
    return failed;
}
int main(void)
{
    OleInitialize(NULL);
    int failures = 0;
    const wchar_t *url = L"https://ptlogin.4399.com/ptlogin/loginFrame.do";
    if (!login_url_matches(L"https://ptlogin.4399.com/ptlogin/loginFrame.do?x=1", url)
        || login_url_matches(L"https://ptlogin.4399.com.evil.test/ptlogin/loginFrame.do", url)
        || login_url_matches(L"http://ptlogin.4399.com/ptlogin/loginFrame.do", url)
        || login_url_matches(L"https://ptlogin.4399.com/ptlogin/loginFrame.do/other", url)) failures++;
    /* 包装页按版本发布，来源门禁必须随版本变化，同时仍拒绝相似域名和非 HTTPS。 */
    if (!trusted_game_page_url(L"https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3680d.htm")
        || !trusted_game_page_url(L"https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3690g.htm")
        || !trusted_game_page_url(L"https://sda.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3702c.htm")
        || trusted_game_page_url(L"https://sbai.4399.com.evil.test/4399swf/upload_swf/gun/v3690g.htm")
        || trusted_game_page_url(L"http://sbai.4399.com/4399swf/upload_swf/gun/v3690g.htm")
        || trusted_game_page_url(L"https://sbai.4399.com/flash/130396.htm")
        || trusted_game_page_url(L"https://sbai.4399.com/")
        || trusted_game_page_url(NULL)) failures++;
    wcscpy(g_login_username, L"synthetic-A"); wcscpy(g_login_password, L"test-'quoted'");
    IHTMLDocument2 *a = fixture(L"<form action='https://ptlogin.4399.com/ptlogin/login.do?v=1'><input id='username'><input type='password' id='j-password'></form>");
    if (!a) return 2;
    if (fill_login_document(a)) failures++; /* about:blank 不得经过生产来源门禁。 */
    BSTR local_url = NULL;
    IHTMLDocument2_get_URL(a, &local_url);
    if (!fill_login_document_at_url(a, local_url)
        || !has_value(a, L"username", g_login_username) || !has_value(a, L"j-password", g_login_password)) failures++;
    wcscpy(g_login_username, L"synthetic-B");
    if (fill_login_document_at_url(a, local_url) || !has_value(a, L"username", L"synthetic-A")) failures++;
    IHTMLDocument2 *b = fixture(L"<form action='https://evil.test/login'><input id='username'><input type='password' id='j-password'></form>");
    if (!b || fill_login_document_at_url(b, local_url)) failures++;
    IHTMLDocument2 *c = fixture(L"<form action='https://ptlogin.4399.com/ptlogin/login.do'><input id='username' value='manual'><input type='password' id='j-password'></form>");
    if (!c || fill_login_document_at_url(c, local_url) || !has_value(c, L"username", L"manual")) failures++;
    /* 网站用 value 模拟占位提示，视觉为空时也应允许填充。 */
    IHTMLDocument2 *hint = fixture(L"<form action='https://ptlogin.4399.com/ptlogin/login.do'><input id='username' value='请输入4399账号或手机号'><input type='password' id='j-password'></form>");
    if (!hint || !fill_login_document_at_url(hint, local_url)
        || !has_value(hint, L"username", g_login_username) || !has_value(hint, L"j-password", g_login_password)) failures++;
    if (hint) IHTMLDocument2_Release(hint);
    IHTMLDocument2 *manual_password = fixture(L"<form action='https://ptlogin.4399.com/ptlogin/login.do'><input id='username' value='请输入4399账号或手机号'><input type='password' id='j-password' value='manual-password'></form>");
    if (!manual_password || fill_login_document_at_url(manual_password, local_url)
        || !has_value(manual_password, L"j-password", L"manual-password")
        || !has_value(manual_password, L"username", L"请输入4399账号或手机号")) failures++;
    if (manual_password) IHTMLDocument2_Release(manual_password);
    /* 根相对 action 必须同时满足固定来源和无 base 标签，拒绝域名/路径近似项。 */
    IHTMLDocument3 *relative_doc = NULL;
    IHTMLDocument2_QueryInterface(a, &IID_IHTMLDocument3, (void **)&relative_doc);
    if (!relative_doc
        || !login_action_matches(relative_doc, url, L"/ptlogin/login.do?v=1")
        || login_action_matches(relative_doc, L"https://evil.test/ptlogin/loginFrame.do", L"/ptlogin/login.do")
        || login_action_matches(relative_doc, url, L"//evil.test/ptlogin/login.do")
        || login_action_matches(relative_doc, url, L"/ptlogin/login.do/other")) failures++;
    if (relative_doc) IHTMLDocument3_Release(relative_doc);
    IHTMLDocument2 *base = fixture(L"<head><base href='https://evil.test/'></head><body></body>");
    relative_doc = NULL;
    if (base) IHTMLDocument2_QueryInterface(base, &IID_IHTMLDocument3, (void **)&relative_doc);
    if (!relative_doc || login_action_matches(relative_doc, url, L"/ptlogin/login.do")) failures++;
    if (relative_doc) IHTMLDocument3_Release(relative_doc);
    if (base) IHTMLDocument2_Release(base);
    failures += verify_submit(L"<form action='https://ptlogin.4399.com/ptlogin/login.do' onsubmit='return false'><input id='username'><input type='password' id='j-password'><input id='j-login-submit-btn' type='submit'></form>", 1);
    failures += verify_submit(L"<form action='https://ptlogin.4399.com/ptlogin/login.do' onsubmit='return false'><input id='username'><input type='password' id='j-password'><input id='j-login-submit-btn' type='submit' disabled></form>", 0);
    failures += verify_submit(L"<form action='https://ptlogin.4399.com/ptlogin/login.do'><input id='username'><input type='password' id='j-password'></form><form action='https://evil.test/' onsubmit='return false'><input id='j-login-submit-btn' type='submit'></form>", 0);
    wchar_t decoded[8];
    /* 晚于首轮探测才出现的表单仍可填充，验证不依赖单次完成事件。 */
    IHTMLDocument2 *delayed = fixture(L"<body>loading</body>");
    if (!delayed || visit_login_frames_at_url(delayed, 0, local_url)) failures++;
    if (delayed)
    {
        IHTMLElement *body = NULL;
        IHTMLDocument2_get_body(delayed, &body);
        BSTR html = SysAllocString(L"<form action='https://ptlogin.4399.com/ptlogin/login.do'><input id='username'><input id='j-password' type='password'></form>");
        if (body) { IHTMLElement_put_innerHTML(body, html); IHTMLElement_Release(body); }
        SysFreeString(html);
        if (!visit_login_frames_at_url(delayed, 0, local_url) || !has_value(delayed, L"username", L"synthetic-B")) failures++;
        IHTMLDocument2_Release(delayed);
    }
    if (!decode_login_hex("410022000A00", decoded, 8) || wcscmp(decoded, L"A\"\n")
        || decode_login_hex("GG00", decoded, 8) || decode_login_hex("0000", decoded, 8)
        || decode_login_hex("41004200", decoded, 2)) failures++;
    if (c) IHTMLDocument2_Release(c);
    if (b) IHTMLDocument2_Release(b);
    IHTMLDocument2_Release(a); SysFreeString(local_url);
    OleUninitialize();
    printf("autofill-dom-probe: %s (%d failures)\n", failures ? "FAIL" : "PASS", failures);
    return failures ? 1 : 0;
}
