/* 当前浏览器专属的登录填充与单次提交：只处理已核实的 HTTPS 表单。 */
#include <ocidl.h>
#include <mshtml.h>

static wchar_t g_login_username[257];
static wchar_t g_login_password[1025];
static IConnectionPoint *g_login_connection;
static DWORD g_login_cookie;
static BOOL g_login_done;
/* 正式入口开启；诊断入口默认关闭，公开页面探针不会提交虚构凭据。 */
static BOOL g_login_auto_submit;

/* 点击平台原有提交控件，保留其 onsubmit 加密/验证码逻辑，绝不直接 form.submit。 */
static void click_login_submit(IHTMLDocument3 *document, IUnknown *expected_form)
{
    BSTR id = SysAllocString(L"j-login-submit-btn"), type = NULL;
    IHTMLElement *element = NULL;
    IHTMLInputElement *input = NULL;
    IHTMLFormElement *form = NULL;
    IUnknown *identity = NULL;
    VARIANT_BOOL disabled = VARIANT_TRUE;
    IHTMLDocument3_getElementById(document, id, &element);
    if (element) IHTMLElement_QueryInterface(element, &IID_IHTMLInputElement, (void **)&input);
    if (input)
    {
        IHTMLInputElement_get_type(input, &type);
        IHTMLInputElement_get_disabled(input, &disabled);
        IHTMLInputElement_get_form(input, &form);
    }
    if (form) IHTMLFormElement_QueryInterface(form, &IID_IUnknown, (void **)&identity);
    if (identity == expected_form && identity && type && !_wcsicmp(type, L"submit") && !disabled)
        IHTMLElement_click(element);
    SysFreeString(id); SysFreeString(type);
    if (identity) IUnknown_Release(identity);
    if (form) IHTMLFormElement_Release(form);
    if (input) IHTMLInputElement_Release(input);
    if (element) IHTMLElement_Release(element);
}

/* URL 采用完整前缀和边界匹配，拒绝相似域名、路径后缀及非 HTTPS 页面。 */
static BOOL login_url_matches(const wchar_t *url, const wchar_t *expected)
{
    if (url == NULL) return FALSE;
    size_t length = wcslen(expected);
    return wcsncmp(url, expected, length) == 0
        && (url[length] == 0 || url[length] == L'?' || url[length] == L'#');
}

/* MSHTML 可原样返回根相对 action。只在固定 HTTPS 登录页、且没有 base 标签
 * 改写解析基址时接受这一个路径；不接受任意相对路径或协议相对外站地址。 */
static BOOL login_action_matches(IHTMLDocument3 *document, const wchar_t *url, const wchar_t *action)
{
    if (login_url_matches(action, L"https://ptlogin.4399.com/ptlogin/login.do")) return TRUE;
    if (!login_url_matches(url, L"https://ptlogin.4399.com/ptlogin/loginFrame.do")
        || !login_url_matches(action, L"/ptlogin/login.do")) return FALSE;
    IHTMLElementCollection *bases = NULL;
    BSTR tag = SysAllocString(L"base");
    HRESULT result = IHTMLDocument3_getElementsByTagName(document, tag, &bases);
    SysFreeString(tag);
    long count = -1;
    if (SUCCEEDED(result) && bases) result = IHTMLElementCollection_get_length(bases, &count);
    if (bases) IHTMLElementCollection_Release(bases);
    return SUCCEEDED(result) && count == 0;
}

/* 从真实框架文档取得字段，不使用父页脚本跨域、坐标或当前前台窗口。 */
static BOOL fill_login_document_at_url(IHTMLDocument2 *document, const wchar_t *expected_url)
{
    BSTR url = NULL;
    IHTMLDocument3 *document3 = NULL;
    IHTMLElement *element = NULL;
    IHTMLInputElement *username = NULL, *password = NULL;
    IHTMLFormElement *user_form = NULL, *password_form = NULL;
    IUnknown *user_identity = NULL, *password_identity = NULL;
    BSTR name = NULL, value = NULL, action = NULL, type = NULL;
    BOOL filled = FALSE;
    VARIANT_BOOL disabled = VARIANT_FALSE, read_only = VARIANT_FALSE;
    if (!g_login_username[0] || !g_login_password[0]) return FALSE;
    if (FAILED(IHTMLDocument2_get_URL(document, &url))
        || !login_url_matches(url, expected_url)) goto done;
    /* 自动提交必须等本页脚本加载完成，避免 NavigateComplete 时校验函数尚未定义。 */
    if (g_login_auto_submit)
    {
        BSTR ready = NULL;
        HRESULT status = IHTMLDocument2_get_readyState(document, &ready);
        BOOL complete = SUCCEEDED(status) && ready && !_wcsicmp(ready, L"complete");
        SysFreeString(ready);
        if (!complete) goto done;
    }
    if (FAILED(IHTMLDocument2_QueryInterface(document, &IID_IHTMLDocument3, (void **)&document3))) goto done;
    name = SysAllocString(L"username");
    IHTMLDocument3_getElementById(document3, name, &element);
    SysFreeString(name); name = NULL;
    if (!element) goto done;
    IHTMLElement_QueryInterface(element, &IID_IHTMLInputElement, (void **)&username);
    IHTMLElement_Release(element); element = NULL;
    name = SysAllocString(L"j-password");
    IHTMLDocument3_getElementById(document3, name, &element);
    SysFreeString(name); name = NULL;
    if (!element) goto done;
    IHTMLElement_QueryInterface(element, &IID_IHTMLInputElement, (void **)&password);
    if (!username || !password) goto done;
    IHTMLInputElement_get_type(username, &type);
    if (!type || _wcsicmp(type, L"text")) goto done;
    SysFreeString(type); type = NULL;
    IHTMLInputElement_get_type(password, &type);
    if (!type || _wcsicmp(type, L"password")) goto done;
    IHTMLInputElement_get_form(username, &user_form);
    IHTMLInputElement_get_form(password, &password_form);
    if (!user_form || !password_form) goto done;
    IHTMLFormElement_QueryInterface(user_form, &IID_IUnknown, (void **)&user_identity);
    IHTMLFormElement_QueryInterface(password_form, &IID_IUnknown, (void **)&password_identity);
    if (!user_identity || user_identity != password_identity) goto done;
    IHTMLFormElement_get_action(user_form, &action);
    if (!login_action_matches(document3, url, action)) goto done;
    IHTMLInputElement_get_disabled(username, &disabled);
    IHTMLInputElement_get_readOnly(username, &read_only);
    if (disabled || read_only) goto done;
    IHTMLInputElement_get_disabled(password, &disabled);
    IHTMLInputElement_get_readOnly(password, &read_only);
    if (disabled || read_only) goto done;
    /* 网站以 value 模拟占位符，并非 HTML placeholder；只豁免这段已核实提示，
     * 其余非空值仍视为用户/平台已有输入，禁止覆盖。读取失败也不尝试写入。 */
    if (FAILED(IHTMLInputElement_get_value(username, &value))) goto done;
    if (value && SysStringLen(value) && wcscmp(value, L"请输入4399账号或手机号") != 0) goto done;
    SysFreeString(value); value = NULL;
    if (FAILED(IHTMLInputElement_get_value(password, &value))) goto done;
    if (value && SysStringLen(value)) goto done;
    SysFreeString(value); value = SysAllocString(g_login_username);
    if (!value || FAILED(IHTMLInputElement_put_value(username, value))) goto done;
    SysFreeString(value); value = SysAllocString(g_login_password);
    if (!value || FAILED(IHTMLInputElement_put_value(password, value))) goto done;
    /* 不触发 focus/blur（平台 blur 会发送用户名校验请求），仅取消提示文字的灰色。 */
    IHTMLElement *user_element = NULL;
    IHTMLStyle *style = NULL;
    IHTMLInputElement_QueryInterface(username, &IID_IHTMLElement, (void **)&user_element);
    if (user_element) IHTMLElement_get_style(user_element, &style);
    if (style)
    {
        VARIANT color; VariantInit(&color);
        V_VT(&color) = VT_BSTR; V_BSTR(&color) = SysAllocString(L"#000000");
        IHTMLStyle_put_color(style, color); VariantClear(&color); IHTMLStyle_Release(style);
    }
    if (user_element) IHTMLElement_Release(user_element);
    filled = TRUE;
    if (g_login_auto_submit)
    {
        /* 先锁住本次尝试再点击，防止同步导航事件重入造成重复提交。
         * 密码错误、验证码或按钮不可用时均交还手动处理，不定时重试。 */
        g_login_done = TRUE;
        click_login_submit(document3, user_identity);
    }
done:
    if (value) { SecureZeroMemory(value, SysStringByteLen(value)); SysFreeString(value); }
    SysFreeString(url); SysFreeString(name); SysFreeString(action); SysFreeString(type);
    if (element) IHTMLElement_Release(element);
    if (username) IHTMLInputElement_Release(username);
    if (password) IHTMLInputElement_Release(password);
    if (user_form) IHTMLFormElement_Release(user_form);
    if (password_form) IHTMLFormElement_Release(password_form);
    if (user_identity) IUnknown_Release(user_identity);
    if (password_identity) IUnknown_Release(password_identity);
    if (document3) IHTMLDocument3_Release(document3);
    return filled;
}

/* 生产入口固定来源；参数化内部函数仅供离线 DOM 夹具验证，不接受外部 URL 配置。 */
static BOOL fill_login_document(IHTMLDocument2 *document)
{
    return fill_login_document_at_url(document, L"https://ptlogin.4399.com/ptlogin/loginFrame.do");
}

/* 登录 iframe 可能先创建后加载，第三方资源也可能延迟 DocumentComplete。
 * 有界遍历只访问当前浏览器自己的 frame，仍逐个核对文档来源和表单 action。
 */
static BOOL visit_login_frames_at_url(IHTMLDocument2 *document, unsigned int depth, const wchar_t *expected_url)
{
    if (fill_login_document_at_url(document, expected_url)) return TRUE;
    if (depth >= 4) return FALSE;
    IHTMLFramesCollection2 *frames = NULL;
    long count = 0;
    IHTMLDocument2_get_frames(document, &frames);
    if (!frames) return FALSE;
    IHTMLFramesCollection2_get_length(frames, &count);
    BOOL filled = FALSE;
    for (long i = 0; i < count && i < 16 && !filled; i++)
    {
        VARIANT index, child;
        VariantInit(&index); VariantInit(&child);
        V_VT(&index) = VT_I4; V_I4(&index) = i;
        if (SUCCEEDED(IHTMLFramesCollection2_item(frames, &index, &child)) && V_VT(&child) == VT_DISPATCH)
        {
            IHTMLWindow2 *window = NULL;
            IHTMLDocument2 *child_document = NULL;
            IDispatch_QueryInterface(V_DISPATCH(&child), &IID_IHTMLWindow2, (void **)&window);
            if (window) IHTMLWindow2_get_document(window, &child_document);
            if (child_document) { filled = visit_login_frames_at_url(child_document, depth + 1, expected_url); IHTMLDocument2_Release(child_document); }
            if (window) IHTMLWindow2_Release(window);
        }
        VariantClear(&child);
    }
    IHTMLFramesCollection2_Release(frames);
    return filled;
}

/* 只在已核实的游戏包装页调用平台公开 UI 函数，参数为空，绝不将密码放进 iframe URL。 */
static void prepare_login_ui(IHTMLDocument2 *document)
{
    BSTR url = NULL;
    IHTMLDocument2_get_URL(document, &url);
    BOOL trusted = login_url_matches(url, L"https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3680d.htm");
    SysFreeString(url);
    if (!trusted) return;
    IHTMLWindow2 *window = NULL;
    IHTMLDocument2_get_parentWindow(document, &window);
    if (!window) return;
    BSTR script = SysAllocString(L"(function(){try{var r=document.documentElement;if(!r||r.getAttribute('data-bqtj-opened'))return;if(typeof UniLogin==='undefined'||typeof UniLogin.showPopupUsernameLogin!=='function'||typeof unionLoginProps==='undefined'||!unionLoginProps.__selfDomain)return;if(UniLogin.getUid&&UniLogin.getUid())return;UniLogin.showPopupUsernameLogin('','',true);if(document.getElementById('popup_login_frame'))r.setAttribute('data-bqtj-opened','1');}catch(e){}})();");
    BSTR language = SysAllocString(L"JavaScript");
    VARIANT result; VariantInit(&result);
    IHTMLWindow2_execScript(window, script, language, &result);
    VariantClear(&result); SysFreeString(script); SysFreeString(language);
    IHTMLWindow2_Release(window);
}

static BOOL login_tick(IWebBrowser2 *browser)
{
    if (g_login_done || !g_login_username[0] || !g_login_password[0]) return TRUE;
    IDispatch *dispatch = NULL;
    IHTMLDocument2 *document = NULL;
    IWebBrowser2_get_Document(browser, &dispatch);
    if (dispatch) IDispatch_QueryInterface(dispatch, &IID_IHTMLDocument2, (void **)&document);
    if (document)
    {
        prepare_login_ui(document);
        g_login_done = visit_login_frames_at_url(document, 0, L"https://ptlogin.4399.com/ptlogin/loginFrame.do");
        IHTMLDocument2_Release(document);
    }
    if (dispatch) IDispatch_Release(dispatch);
    return g_login_done;
}

/* COM 事件对象静态存活到进程退出；解绑连接点后才销毁浏览器。 */
static HRESULT STDMETHODCALLTYPE login_query(IDispatch *self, REFIID iid, void **result)
{
    if (!result) return E_POINTER;
    *result = NULL;
    if (IsEqualIID(iid, &IID_IUnknown) || IsEqualIID(iid, &IID_IDispatch) || IsEqualIID(iid, &DIID_DWebBrowserEvents2))
    { *result = self; return S_OK; }
    return E_NOINTERFACE;
}
static ULONG STDMETHODCALLTYPE login_addref(IDispatch *self) { (void)self; return 2; }
static ULONG STDMETHODCALLTYPE login_release(IDispatch *self) { (void)self; return 1; }
static HRESULT STDMETHODCALLTYPE login_type_count(IDispatch *self, UINT *count) { (void)self; *count = 0; return S_OK; }
static HRESULT STDMETHODCALLTYPE login_type(IDispatch *self, UINT index, LCID locale, ITypeInfo **info)
{ (void)self; (void)index; (void)locale; (void)info; return E_NOTIMPL; }
static HRESULT STDMETHODCALLTYPE login_names(IDispatch *self, REFIID iid, LPOLESTR *names, UINT count, LCID locale, DISPID *ids)
{ (void)self; (void)iid; (void)names; (void)count; (void)locale; (void)ids; return E_NOTIMPL; }
static HRESULT STDMETHODCALLTYPE login_invoke(IDispatch *self, DISPID id, REFIID iid, LCID locale, WORD flags,
    DISPPARAMS *args, VARIANT *result, EXCEPINFO *exception, UINT *error)
{
    (void)self; (void)iid; (void)locale; (void)flags; (void)result; (void)exception; (void)error;
    /* DocumentComplete=259。每个 iframe 完成时传入它自己的浏览器对象。 */
    if (!g_login_done && (id == 259 || id == 252) && args && args->cArgs == 2 && V_VT(&args->rgvarg[1]) == VT_DISPATCH)
    {
        IWebBrowser2 *browser = NULL;
        IDispatch *dispatch = NULL;
        IHTMLDocument2 *document = NULL;
        IDispatch_QueryInterface(V_DISPATCH(&args->rgvarg[1]), &IID_IWebBrowser2, (void **)&browser);
        if (browser) IWebBrowser2_get_Document(browser, &dispatch);
        if (dispatch) IDispatch_QueryInterface(dispatch, &IID_IHTMLDocument2, (void **)&document);
        if (document) { g_login_done = fill_login_document(document); IHTMLDocument2_Release(document); }
        if (dispatch) IDispatch_Release(dispatch);
        if (browser) IWebBrowser2_Release(browser);
    }
    return S_OK;
}
static IDispatchVtbl g_login_vtable = { login_query, login_addref, login_release, login_type_count, login_type, login_names, login_invoke };
static IDispatch g_login_sink = { &g_login_vtable };

static void detach_login_events(void)
{
    if (g_login_connection)
    {
        IConnectionPoint_Unadvise(g_login_connection, g_login_cookie);
        IConnectionPoint_Release(g_login_connection);
        g_login_connection = NULL;
    }
}
static void attach_login_events(IWebBrowser2 *browser)
{
    IConnectionPointContainer *container = NULL;
    if (SUCCEEDED(IWebBrowser2_QueryInterface(browser, &IID_IConnectionPointContainer, (void **)&container)))
    {
        if (SUCCEEDED(IConnectionPointContainer_FindConnectionPoint(container, &DIID_DWebBrowserEvents2, &g_login_connection)))
            IConnectionPoint_Advise(g_login_connection, (IUnknown *)&g_login_sink, &g_login_cookie);
        IConnectionPointContainer_Release(container);
    }
}

/* 管道只接受限长 UTF-16 十六进制文本，避免分隔符/换行变成控制命令。 */
static BOOL decode_login_hex(const char *text, wchar_t *output, size_t capacity)
{
    size_t length = strlen(text);
    if (length % 4 || length / 4 >= capacity) return FALSE;
    for (size_t i = 0; i < length / 4; i++)
    {
        unsigned int bytes[2] = {0, 0};
        for (int j = 0; j < 4; j++)
        {
            char c = text[i * 4 + j];
            int digit = c >= '0' && c <= '9' ? c-'0' : c >= 'A' && c <= 'F' ? c-'A'+10 : -1;
            if (digit < 0) return FALSE;
            bytes[j / 2] = bytes[j / 2] * 16 + (unsigned int)digit;
        }
        output[i] = (wchar_t)(bytes[0] | (bytes[1] << 8));
        if (!output[i]) return FALSE;
    }
    output[length / 4] = 0;
    return TRUE;
}
