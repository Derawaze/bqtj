#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <MaaFramework/MaaAPI.h>
#include <algorithm>
#include <fstream>
#include <iostream>
#include <io.h>
#include <map>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

/* 独立x64视觉进程：一个OCR资源、串行识别请求、各窗口独立截图缓存。
 * 只接受父进程给出的目标，不枚举桌面、不激活窗口、不移动系统光标。 */
#define API_LIST(X) \
 X(MaaGlobalSetOption) X(MaaResourceCreate) X(MaaResourceDestroy) X(MaaResourcePostBundle) X(MaaResourceStatus) \
 X(MaaResourceOverrideImage) X(MaaResourceSetOption) X(MaaTaskerCreate) X(MaaTaskerDestroy) X(MaaTaskerBindResource) X(MaaTaskerClearCache) \
 X(MaaTaskerPostRecognition) X(MaaTaskerStatus) X(MaaTaskerGetTaskDetail) X(MaaTaskerGetNodeDetail) X(MaaTaskerGetRecognitionDetail) \
 X(MaaStringBufferCreate) X(MaaStringBufferDestroy) X(MaaStringBufferGet) \
 X(MaaImageBufferCreate) X(MaaImageBufferDestroy) X(MaaImageBufferSetEncoded) X(MaaImageBufferSetRawData) \
 X(MaaImageBufferGetRawData) X(MaaImageBufferWidth) X(MaaImageBufferHeight) X(MaaImageBufferChannels) X(MaaImageBufferResize) \
 X(MaaWin32ControllerCreate) X(MaaControllerDestroy) X(MaaControllerSetOption) \
 X(MaaControllerPostConnection) X(MaaControllerPostScreencap) X(MaaControllerStatus) X(MaaControllerCachedImage)
struct Api {
#define DECLARE(name) decltype(&::name) name = nullptr;
    API_LIST(DECLARE)
#undef DECLARE
} api;
static long long timestamp() { LARGE_INTEGER value; QueryPerformanceCounter(&value); return value.QuadPart; }
static std::string utf8(const std::wstring& value) {
    int n = WideCharToMultiByte(CP_UTF8, 0, value.data(), (int)value.size(), nullptr, 0, nullptr, nullptr);
    std::string result(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, value.data(), (int)value.size(), result.data(), n, nullptr, nullptr);
    return result;
}
static std::string hex(const unsigned char* data, size_t count) {
    static const char* digits = "0123456789ABCDEF";
    std::string result(count * 2, 0);
    for (size_t i = 0; i < count; ++i) { result[i * 2] = digits[data[i] >> 4]; result[i * 2 + 1] = digits[data[i] & 15]; }
    return result;
}
static std::vector<unsigned char> unhex(const std::string& text) {
    if (text.size() % 2) throw std::runtime_error("protocol");
    auto digit = [](char c) -> int { if (c >= '0' && c <= '9') return c - '0'; if (c >= 'A' && c <= 'F') return c - 'A' + 10; throw std::runtime_error("protocol"); };
    std::vector<unsigned char> result(text.size() / 2);
    for (size_t i = 0; i < result.size(); ++i) result[i] = (unsigned char)(digit(text[i * 2]) * 16 + digit(text[i * 2 + 1]));
    return result;
}
// 异步状态失败保留所属阶段，不能把连接、截图、识别统一抹成vision。
template<class Status> static void wait_done(Status status, const char* failure) {
    auto start = GetTickCount64();
    for (;;) {
        auto state = status();
        if (state == MaaStatus_Succeeded) return;
        if (state != MaaStatus_Pending && state != MaaStatus_Running) throw std::runtime_error(failure);
        if (GetTickCount64() - start > 8000) throw std::runtime_error("vision-timeout");
        Sleep(20);
    }
}
struct Image {
    MaaImageBuffer* value = api.MaaImageBufferCreate();
    ~Image() { if (value) api.MaaImageBufferDestroy(value); }
};
struct Text {
    MaaStringBuffer* value = api.MaaStringBufferCreate();
    ~Text() { if (value) api.MaaStringBufferDestroy(value); }
};
struct Target {
    DWORD pid = 0, container = 0;
    MaaController* controller = nullptr;
    long long snapshot = 0, captured = 0;
    // 仅在紧随悬停的新截图期间续发本窗口消息；短租期结束或其他输入立即清除，不占用系统鼠标。
    POINT hover = {};
    ULONGLONG hover_until = 0;
};
/* 所有输入前都核对Flash子窗口及容器归属；不可见时由上层暂停，不让Maa恢复最小化窗口。 */
static void validate(HWND window, DWORD pid, DWORD container, bool visible = true) {
    wchar_t name[128];
    DWORD actual = 0, root = 0;
    if (!IsWindow(window) || !GetClassNameW(window, name, 128) || std::wstring(name) != L"MacromediaFlashPlayerActiveX")
        throw std::runtime_error("target-changed");
    GetWindowThreadProcessId(window, &actual);
    GetWindowThreadProcessId(GetAncestor(window, GA_ROOT), &root);
    if (actual != pid || root != container) throw std::runtime_error("target-changed");
    if (!visible) return;
    if (!IsWindowVisible(window) || IsIconic(GetAncestor(window, GA_ROOT))) throw std::runtime_error("target-hidden");
    RECT rect;
    if (!GetClientRect(window, &rect) || rect.right != 950 || rect.bottom != 600) throw std::runtime_error("target-size");
}

/* Flash可能在真实鼠标离开时收起弹层；截图前及异步截图等待中维持刚请求的后台悬停。 */
static void refresh_hover(HWND window, Target& target) {
    if (GetTickCount64() >= target.hover_until) return;
    DWORD_PTR result;
    if (!SendMessageTimeoutW(window, WM_MOUSEMOVE, 0, MAKELPARAM(target.hover.x, target.hover.y),
        SMTO_ABORTIFHUNG | SMTO_BLOCK, 1000, &result)) throw std::runtime_error("input-uncertain");
}

static std::string recognize(MaaResource* resource, MaaTasker* tasker, Image& image, const char* algorithm, const std::string& params) {
    api.MaaTaskerClearCache(tasker);
    auto id = api.MaaTaskerPostRecognition(tasker, algorithm, params.c_str(), image.value);
    if (!id) throw std::runtime_error("recognition");
    wait_done([&] { return api.MaaTaskerStatus(tasker, id); }, "recognition");
    Text entry, name, detail;
    MaaNodeId nodes[16]; MaaSize count = 16; MaaStatus status;
    MaaRecoId reco; MaaActId action; MaaBool completed, hit; MaaRect box;
    if (!api.MaaTaskerGetTaskDetail(tasker, id, entry.value, nodes, &count, &status) || count != 1 ||
        !api.MaaTaskerGetNodeDetail(tasker, nodes[0], name.value, &reco, &action, &completed) ||
        !api.MaaTaskerGetRecognitionDetail(tasker, reco, name.value, entry.value, &hit, &box, detail.value, nullptr, nullptr))
        throw std::runtime_error("recognition-detail");
    (void)resource;
    return api.MaaStringBufferGet(detail.value);
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    // 与容器保持物理像素坐标，避免高DPI将950×600虚拟化成另一尺寸。
    SetProcessDPIAware();
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    std::wstring root = argv[1], bin = root + L"\\bin";
    // 动态库静态初始化早于日志选项，临时抑制其标准输出，保证协议不混入第三方日志。
    int protocol_output = _dup(_fileno(stdout));
    freopen("NUL", "w", stdout);
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    AddDllDirectory(bin.c_str());
    HMODULE win32_library = LoadLibraryExW((bin + L"\\MaaWin32ControlUnit.dll").c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    HMODULE library = LoadLibraryExW((bin + L"\\MaaFramework.dll").c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!library) { _dup2(protocol_output, _fileno(stdout)); _close(protocol_output); std::cout << "error component\n" << std::flush; return 3; }
    MaaResource* resource = nullptr;
    MaaTasker* tasker = nullptr;
    std::map<HWND, Target> targets;
    try {
#define LOAD(name) api.name = (decltype(api.name))GetProcAddress(library, #name); if (!api.name) throw std::runtime_error("component-api");
        API_LIST(LOAD)
#undef LOAD
        /* 关闭第三方日志和截图落盘，原始画面与OCR只在本进程内处理。 */
        unsigned char disabled = 0; int log_level = 0;
        for (int option : {2, 6, 7}) api.MaaGlobalSetOption((MaaGlobalOption)option, &disabled, 1);
        api.MaaGlobalSetOption((MaaGlobalOption)4, &log_level, 4);
        _dup2(protocol_output, _fileno(stdout)); _close(protocol_output); protocol_output = -1;
        resource = api.MaaResourceCreate(); tasker = api.MaaTaskerCreate();
        if (!resource || !tasker || !api.MaaTaskerBindResource(tasker, resource)) throw std::runtime_error("component");
        // 首版固定CPU推理，不携带DirectML或额外GPU运行时。
        int provider = MaaInferenceExecutionProvider_CPU;
        if (!api.MaaResourceSetOption(resource, MaaResOption_InferenceExecutionProvider, &provider, sizeof(provider))) throw std::runtime_error("component-cpu");
        auto bundle = utf8(root + L"\\ppocr-v4-zh-cn");
        auto load = api.MaaResourcePostBundle(resource, bundle.c_str());
        wait_done([&] { return api.MaaResourceStatus(resource, load); }, "component");
        std::cout << "ready\n" << std::flush;
        std::string line;
        while (std::getline(std::cin, line)) {
            std::istringstream input(line);
            std::string operation; long long id = 0, handle = 0, snapshot = 0;
            DWORD pid = 0, container = 0; int x = 0, y = 0, width = 0, height = 0;
            input >> operation >> id >> pid >> container >> handle >> x >> y >> width >> height >> snapshot;
            HWND window = (HWND)(intptr_t)handle;
            try {
                if (!input || !id) throw std::runtime_error("protocol");
                if (operation == "release") {
                    auto found = targets.find(window);
                    if (found != targets.end()) { api.MaaControllerDestroy(found->second.controller); targets.erase(found); }
                    std::cout << "reply " << id << " ok 0 7B7D\n" << std::flush;
                    continue;
                }
                validate(window, pid, container);
                if (operation == "key" || operation == "keys" || operation == "battle-ai" || operation == "battle-ai-rise") {
                    if (auto found = targets.find(window); found != targets.end()) found->second.hover_until = 0;
                    // N仅属于具名AI开关，不扩大通用走位接口的按键范围。
                    bool battle_ai = operation == "battle-ai";
                    bool rising = operation == "battle-ai-rise";
                    if ((rising ? x != 'N' || y != 'W' || width < 50 : battle_ai ? x != 'N' || y != 0 || width != 50 : x != 'W' && x != 'A' && x != 'S' && x != 'D')
                        || width < 1 || width > 500) throw std::runtime_error("coordinates");
                    bool diagonal = operation == "keys";
                    if (diagonal && !(((x == 'W' || x == 'S') && (y == 'A' || y == 'D')) ||
                        ((x == 'A' || x == 'D') && (y == 'W' || y == 'S')))) throw std::runtime_error("coordinates");
                    int keys[2] = {x, y}, count = diagonal || rising ? 2 : 1; DWORD_PTR result;
                    bool held[2] = {false, false};
                    auto send_key = [&](int key, bool up) {
                        auto scan = MapVirtualKeyW((UINT)key, MAPVK_VK_TO_VSC);
                        LPARAM flags = 1 | (scan << 16);
                        if (up) flags |= (1LL << 30) | (1LL << 31);
                        return SendMessageTimeoutW(window, up ? WM_KEYUP : WM_KEYDOWN, (WPARAM)key, flags,
                            SMTO_ABORTIFHUNG | SMTO_BLOCK, 200, &result) != 0;
                    };
                    // 任一按下/校验失败也释放两键，不能因第二键失败留下第一键持续移动。
                    auto release_one = [&](int i) { if (!held[i]) return true; if (!send_key(keys[i], true)) return false; held[i] = false; return true; };
                    auto release = [&] { bool ok = true; for (int i = count - 1; i >= 0; --i) if (!release_one(i)) ok = false; return ok; };
                    try {
                        for (int i = 0; i < count; ++i) { held[i] = true; if (!send_key(keys[i], false)) throw std::runtime_error("input-uncertain"); }
                        // N和W一起按下，50ms先释放N，剩余时间继续上升；最后统一保证所有键已松开。
                        if (rising) { Sleep(50); if (!release_one(0)) throw std::runtime_error("input-uncertain"); Sleep((DWORD)(width - 50)); }
                        else Sleep((DWORD)width);
                        validate(window, pid, container, false);
                    } catch (...) { release(); throw; }
                    if (!release()) throw std::runtime_error("input-uncertain");
                    std::cout << "reply " << id << " ok 0 7B7D\n" << std::flush;
                    continue;
                }
                if (operation == "click" || operation == "hover") {
                    if (x < 0 || y < 0 || x >= 950 || y >= 600) throw std::runtime_error("coordinates");
                    LPARAM coordinates = MAKELPARAM(x, y); DWORD_PTR result;
                    auto send = [&](UINT message, WPARAM buttons) {
                        if (!SendMessageTimeoutW(window, message, buttons, coordinates, SMTO_ABORTIFHUNG | SMTO_BLOCK, 1000, &result))
                            throw std::runtime_error("input-uncertain");
                    };
                    send(WM_MOUSEMOVE, 0);
                    if (operation == "hover") {
                        auto& target = targets[window];
                        target.hover = {x, y}; target.hover_until = GetTickCount64() + 1000;
                        std::cout << "reply " << id << " ok 0 7B7D\n" << std::flush;
                        continue;
                    }
                    if (auto found = targets.find(window); found != targets.end()) found->second.hover_until = 0;
                    try { send(WM_LBUTTONDOWN, MK_LBUTTON); Sleep(50); }
                    catch (...) { validate(window, pid, container, false); SendMessageTimeoutW(window, WM_LBUTTONUP, 0, coordinates, SMTO_ABORTIFHUNG | SMTO_BLOCK, 1000, &result); throw; }
                    validate(window, pid, container, false);
                    send(WM_LBUTTONUP, 0);
                    std::cout << "reply " << id << " ok 0 7B7D\n" << std::flush;
                    continue;
                }
                if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 950 || y + height > 600) throw std::runtime_error("coordinates");
                auto& target = targets[window];
                if (!target.controller) {
                    target.pid = pid; target.container = container;
                    target.controller = api.MaaWin32ControllerCreate(window, 16, 4, 4);
                    unsigned char raw = 1;
                    if (!target.controller || !api.MaaControllerSetOption(target.controller, (MaaCtrlOption)3, &raw, 1))
                        throw std::runtime_error("capture");
                    auto connect = api.MaaControllerPostConnection(target.controller);
                    wait_done([&] { return api.MaaControllerStatus(target.controller, connect); }, "controller-connect");
                }
                if (target.pid != pid || target.container != container) throw std::runtime_error("target-changed");
                if (!snapshot) {
                    refresh_hover(window, target);
                    auto capture = api.MaaControllerPostScreencap(target.controller);
                    wait_done([&] { refresh_hover(window, target); return api.MaaControllerStatus(target.controller, capture); }, "capture");
                    target.snapshot = id; target.captured = timestamp();
                } else if (snapshot != target.snapshot) throw std::runtime_error("snapshot");
                validate(window, pid, container);
                Image frame;
                if (!api.MaaControllerCachedImage(target.controller, frame.value) ||
                    api.MaaImageBufferWidth(frame.value) != 950 || api.MaaImageBufferHeight(frame.value) != 600 ||
                    api.MaaImageBufferChannels(frame.value) != 3) throw std::runtime_error("capture");
                const auto* source = (const unsigned char*)api.MaaImageBufferGetRawData(frame.value);
                std::vector<unsigned char> pixels(width * height * 3);
                for (int row = 0; row < height; ++row)
                    std::copy_n(source + ((y + row) * 950 + x) * 3, width * 3, pixels.data() + row * width * 3);
                std::string detail = "{}", extra;
                if (operation == "pixels") extra = hex(pixels.data(), pixels.size());
                else {
                    Image crop;
                    if (operation == "green" || operation == "save-name" || operation == "quota-counter") {
                        const int mask_scale = operation == "save-name" ? 1 : 3;
                        std::vector<unsigned char> mask(width * height * 3 * mask_scale * mask_scale, 255);
                        for (int py = 0; py < height; ++py) for (int px = 0; px < width; ++px) {
                            auto p = &pixels[(py * width + px) * 3];
                            // 存档名/等级为黄色，通关标记和时间为灰色；去背景后3倍插值放大，保留小字号笔画。
                            // 计数已裁到名称右侧小区域，保留白色括号/斜杠及蓝、绿数字，统一黑字白底。
                            bool foreground = operation == "quota-counter" ? std::max({p[0], p[1], p[2]}) >= 100 : operation == "save-name"
                                ? p[2] >= 60 && p[1] >= 60 && p[2] - p[0] >= 30 && p[1] - p[0] >= 30
                                : p[1] >= 70 && p[1] - p[2] >= 40 && p[1] - p[0] >= 35;
                            if (!foreground) continue;
                            auto ink = operation == "save-name" ? (unsigned char)(255 - std::max(p[1], p[2])) : 0;
                            for (int dy = 0; dy < mask_scale; ++dy) for (int dx = 0; dx < mask_scale; ++dx)
                                std::fill_n(&mask[((py * mask_scale + dy) * width * mask_scale + px * mask_scale + dx) * 3], 3, ink);
                        }
                        api.MaaImageBufferSetRawData(crop.value, mask.data(), width * mask_scale, height * mask_scale, 16);
                        if (operation == "save-name") {
                            // 密集汉字与简单笔画对文字极性反应不同，同一快照比较白底/黑底，不改变原始游戏画面。
                            api.MaaImageBufferResize(crop.value, width * 3, height * 3);
                            auto light = recognize(resource, tasker, crop, "OCR", "{\"threshold\":0.6,\"order_by\":\"Horizontal\"}");
                            for (auto& value : mask) value = (unsigned char)(255 - value);
                            api.MaaImageBufferSetRawData(crop.value, mask.data(), width, height, 16);
                            api.MaaImageBufferResize(crop.value, width * 3, height * 3);
                            auto dark = recognize(resource, tasker, crop, "OCR", "{\"threshold\":0.6,\"order_by\":\"Horizontal\"}");
                            detail = "{\"light\":" + light + ",\"dark\":" + dark + "}";
                        } else {
                            detail = recognize(resource, tasker, crop, "OCR", operation == "quota-counter"
                                ? "{\"threshold\":0.6,\"order_by\":\"Horizontal\"}"
                                : "{\"threshold\":0.85,\"order_by\":\"Vertical\"}");
                        }
                    } else {
                        api.MaaImageBufferSetRawData(crop.value, pixels.data(), width, height, 16);
                        if (operation == "image" || operation == "feature-image") {
                            std::string encoded; input >> encoded;
                            auto bytes = unhex(encoded); Image pattern;
                            if (!api.MaaImageBufferSetEncoded(pattern.value, bytes.data(), bytes.size())) throw std::runtime_error("image");
                            // 小物品先同倍率放大，再提取旋转/缩放不变特征；不降低普通模板的匹配门槛。
                            if (operation == "feature-image") {
                                api.MaaImageBufferResize(pattern.value, api.MaaImageBufferWidth(pattern.value) * 3, api.MaaImageBufferHeight(pattern.value) * 3);
                                api.MaaImageBufferResize(crop.value, width * 3, height * 3);
                            }
                            if (!api.MaaResourceOverrideImage(resource, "script-template.png", pattern.value)) throw std::runtime_error("image");
                            detail = operation == "feature-image"
                                ? recognize(resource, tasker, crop, "FeatureMatch", "{\"template\":[\"script-template.png\"],\"count\":4,\"detector\":\"SIFT\",\"ratio\":0.6}")
                                : recognize(resource, tasker, crop, "TemplateMatch", "{\"template\":[\"script-template.png\"],\"threshold\":[0.9],\"method\":5}");
                        } else if (operation == "ocr" || operation == "ocr2") {
                            if (operation == "ocr2") api.MaaImageBufferResize(crop.value, width * 2, height * 2);
                            // 名称行已经过颜色/位置隔离；适当降低小字号置信阈值，避免仅保留等级而丢弃中文名字。
                        detail = recognize(resource, tasker, crop, "OCR", operation == "save-name"
                            ? "{\"threshold\":0.6,\"order_by\":\"Horizontal\"}" : "{\"threshold\":0.85,\"order_by\":\"Vertical\"}");
                        }
                        else throw std::runtime_error("protocol");
                    }
                }
                std::cout << "reply " << id << " ok " << target.captured << " "
                    << hex((const unsigned char*)detail.data(), detail.size());
                if (!extra.empty()) std::cout << " " << extra;
                std::cout << "\n" << std::flush;
            } catch (const std::exception& error) {
                /* 仅返回固定错误码，不输出OCR正文、路径或账号信息。 */
                std::cout << "reply " << id << " error " << error.what() << "\n" << std::flush;
            }
        }
    } catch (const std::exception&) {
        if (protocol_output >= 0) { _dup2(protocol_output, _fileno(stdout)); _close(protocol_output); }
        std::cout << "error component\n" << std::flush;
    }
    for (auto& pair : targets) if (pair.second.controller) api.MaaControllerDestroy(pair.second.controller);
    if (tasker) api.MaaTaskerDestroy(tasker);
    if (resource) api.MaaResourceDestroy(resource);
    FreeLibrary(library);
    if (win32_library) FreeLibrary(win32_library);
    return 0;
}
