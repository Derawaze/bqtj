#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <algorithm>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>
#include <map>

/* 合成窗口仅测试自己的Flash同名子窗口；不枚举、截图或点击用户的游戏。 */
struct KeyCounts { int down = 0, up = 0; };
static std::map<HWND, KeyCounts> window_keys;
static std::map<HWND, bool> hover_visible;
static int clicks = 0;
static int moves = 0, key_downs = 0, key_ups = 0, held_key = 0;
static int held_mask = 0; static bool diagonal_seen = false;
static bool ai_rise_seen = false, n_released_while_rising = false;
static void paint(HWND window, HDC dc) {
    RECT area; GetClientRect(window, &area);
    HBRUSH background = CreateSolidBrush(RGB(15, 23, 35)); FillRect(dc, &area, background); DeleteObject(background);
    HFONT font = CreateFontW(-24, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Microsoft YaHei");
    auto previous = SelectObject(dc, font); SetBkMode(dc, TRANSPARENT); SetTextColor(dc, RGB(235, 239, 247));
    const wchar_t* title = L"读取存档 助理";
    TextOutW(dc, 270, 150, title, lstrlenW(title));
    SetTextColor(dc, RGB(60, 240, 70));
    const wchar_t* save = L"保存存档";
    TextOutW(dc, 770, 450, save, lstrlenW(save));
    for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) {
        RECT square = {400 + col * 10, 250 + row * 10, 410 + col * 10, 260 + row * 10};
        auto brush = CreateSolidBrush((row + col) % 2 ? RGB(230, 90, 50) : RGB(50, 120, 230));
        FillRect(dc, &square, brush); DeleteObject(brush);
    }
    SetTextColor(dc, RGB(255, 230, 0));
    const wchar_t* nickname = L"测试角色 lv.99";
    TextOutW(dc, 280, 350, nickname, lstrlenW(nickname));
    SetTextColor(dc, RGB(130, 130, 130));
    const wchar_t* cleared = L"角色通关";
    TextOutW(dc, 480, 350, cleared, lstrlenW(cleared));
    SetTextColor(dc, RGB(20, 180, 255));
    const wchar_t* counter = L"(26/27)";
    TextOutW(dc, 280, 400, counter, lstrlenW(counter));
    // 模拟鼠标离开后消失的游戏悬停弹层，供后台截图前的悬停续期回归使用。
    RECT hint = {800, 420, 810, 430};
    auto hint_brush = CreateSolidBrush(hover_visible[window] ? RGB(20, 210, 40) : RGB(210, 20, 40));
    FillRect(dc, &hint, hint_brush); DeleteObject(hint_brush);
    SelectObject(dc, previous); DeleteObject(font);
}
static LRESULT CALLBACK procedure(HWND window, UINT message, WPARAM word, LPARAM value) {
    if (message == WM_PAINT) { PAINTSTRUCT state; auto dc = BeginPaint(window, &state); paint(window, dc); EndPaint(window, &state); return 0; }
    if (message == WM_PRINT || message == WM_PRINTCLIENT) { paint(window, (HDC)word); return 0; }
    if (message == WM_LBUTTONDOWN) { clicks++; return 0; }
    if (message == WM_MOUSEMOVE) { moves++; hover_visible[window] = LOWORD(value) == 150 && HIWORD(value) == 480; return 0; }
    if (message == WM_MOUSELEAVE) { hover_visible[window] = false; return 0; }
    if (message == WM_KEYDOWN) {
        window_keys[window].down++;
        key_downs++; held_key = (int)word;
        if (word == 'W') held_mask |= 1; if (word == 'D') held_mask |= 2;
        if (word == 'N') held_mask |= 4;
        if (held_mask == 3) diagonal_seen = true;
        if ((held_mask & 5) == 5) ai_rise_seen = true;
        return 0;
    }
    if (message == WM_KEYUP) {
        window_keys[window].up++;
        key_ups++; held_key = 0;
        if (word == 'N' && (held_mask & 1)) n_released_while_rising = true;
        if (word == 'W') held_mask &= ~1; if (word == 'D') held_mask &= ~2;
        if (word == 'N') held_mask &= ~4;
        return 0;
    }
    return DefWindowProcW(window, message, word, value);
}
static void pump() {
    MSG message;
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
}
static void require(bool value, const char* detail) { if (!value) throw std::runtime_error(detail); }
static DWORD WINAPI stop_reader(void* event) {
    std::string line; std::getline(std::cin, line); SetEvent((HANDLE)event); return 0;
}
static std::string read_line(HANDLE pipe) {
    auto start = GetTickCount64(); std::string line;
    while (GetTickCount64() - start < 45000) {
        pump(); DWORD bytes = 0;
        if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &bytes, nullptr)) throw std::runtime_error("worker-pipe");
        if (!bytes) { Sleep(10); continue; }
        char value; DWORD read;
        if (!ReadFile(pipe, &value, 1, &read, nullptr) || !read) throw std::runtime_error("worker-read");
        if (value == '\n') return line;
        if (value != '\r') line += value;
    }
    throw std::runtime_error("worker-timeout");
}
static std::string unhex(const std::string& text) {
    std::string result;
    for (size_t offset = 0; offset + 1 < text.size(); offset += 2)
        result += (char)std::stoi(text.substr(offset, 2), nullptr, 16);
    return result;
}
static std::string hex(const std::vector<unsigned char>& values) {
    static const char* digits = "0123456789ABCDEF"; std::string result;
    for (auto value : values) { result += digits[value >> 4]; result += digits[value & 15]; }
    return result;
}
static std::string utf8(const std::wstring& value) {
    int n = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), (int)value.size(), nullptr, 0, nullptr, nullptr);
    std::string result(n, 0); WideCharToMultiByte(CP_UTF8, 0, value.c_str(), (int)value.size(), result.data(), n, nullptr, nullptr); return result;
}

/* 合成夹具的宽度取4的倍数，原始BGR行无需额外填充；不包含用户截图。 */
static std::vector<unsigned char> bitmap(const std::string& data, int width, int height) {
    BITMAPFILEHEADER fileHeader = {}; BITMAPINFOHEADER imageHeader = {};
    fileHeader.bfType = 0x4d42; fileHeader.bfOffBits = sizeof(fileHeader) + sizeof(imageHeader); fileHeader.bfSize = fileHeader.bfOffBits + (DWORD)data.size();
    imageHeader.biSize = sizeof(imageHeader); imageHeader.biWidth = width; imageHeader.biHeight = -height; imageHeader.biPlanes = 1; imageHeader.biBitCount = 24;
    std::vector<unsigned char> result(fileHeader.bfSize);
    std::copy_n((unsigned char*)&fileHeader, sizeof(fileHeader), result.data());
    std::copy_n((unsigned char*)&imageHeader, sizeof(imageHeader), result.data() + sizeof(fileHeader));
    std::copy(data.begin(), data.end(), result.begin() + fileHeader.bfOffBits);
    return result;
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 2 || argc > 3) return 2;
    SetProcessDPIAware();
    auto instance = GetModuleHandleW(nullptr);
    WNDCLASSW windowClass = {}; windowClass.lpfnWndProc = procedure; windowClass.hInstance = instance;
    windowClass.lpszClassName = L"BqtjScriptFixtureRoot"; RegisterClassW(&windowClass);
    windowClass.lpszClassName = L"MacromediaFlashPlayerActiveX"; RegisterClassW(&windowClass);
    auto root = CreateWindowW(L"BqtjScriptFixtureRoot", L"脚本视觉合成夹具", WS_OVERLAPPEDWINDOW, 40, 40, 990, 660, nullptr, nullptr, instance, nullptr);
    auto game = CreateWindowW(L"MacromediaFlashPlayerActiveX", L"", WS_CHILD | WS_VISIBLE, 0, 0, 950, 600, root, nullptr, instance, nullptr);
    ShowWindow(root, SW_SHOWNOACTIVATE); UpdateWindow(root); pump();
    // 托管协议夹具只借用本进程窗口；stdin关闭后结束，不读取或绑定任何用户窗口。
    if (std::wstring(argv[1]) == L"--serve") {
        HANDLE stopped = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        HANDLE reader = CreateThread(nullptr, 0, stop_reader, stopped, 0, nullptr);
        std::cout << "target " << GetCurrentProcessId() << " " << (long long)(intptr_t)game << "\n" << std::flush;
        while (WaitForSingleObject(stopped, 0) != WAIT_OBJECT_0) { pump(); Sleep(10); }
        WaitForSingleObject(reader, 1000); CloseHandle(reader); CloseHandle(stopped); DestroyWindow(root); return 0;
    }
    HANDLE inputRead, inputWrite, outputRead, outputWrite;
    SECURITY_ATTRIBUTES security = {sizeof(security), nullptr, TRUE};
    CreatePipe(&inputRead, &inputWrite, &security, 0); CreatePipe(&outputRead, &outputWrite, &security, 0);
    SetHandleInformation(inputWrite, HANDLE_FLAG_INHERIT, 0); SetHandleInformation(outputRead, HANDLE_FLAG_INHERIT, 0);
    STARTUPINFOW startup = {}; startup.cb = sizeof(startup); startup.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE; startup.hStdInput = inputRead; startup.hStdOutput = outputWrite; startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    PROCESS_INFORMATION process = {};
    std::wstring command = L"\"" + std::wstring(argv[1]) + L"\\BqtjScriptVision.exe\" \"" + argv[1] + L"\"";
    if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW, nullptr, argv[1], &startup, &process)) return 3;
    CloseHandle(inputRead); CloseHandle(outputWrite);
    int result = 1;
    try {
        require(read_line(outputRead) == "ready", "model-start");
        long long id = 0;
        auto request = [&](const std::string& operation, int x, int y, int width, int height, long long snapshot = 0,
                           const std::string& extra = "", DWORD pid = 0) {
            std::ostringstream line;
            line << operation << " " << ++id << " " << (pid ? pid : GetCurrentProcessId()) << " " << GetCurrentProcessId()
                << " " << (long long)(intptr_t)game << " " << x << " " << y << " " << width << " " << height << " " << snapshot;
            if (!extra.empty()) line << " " << extra;
            line << "\n"; auto text = line.str(); DWORD written;
            require(WriteFile(inputWrite, text.data(), (DWORD)text.size(), &written, nullptr) != FALSE, "command-write");
            return read_line(outputRead);
        };
        // 首页等候区可能没有文字：空结果应可继续观察，不能当成组件损坏而中断队列。
        auto response = request("ocr2", 10, 80, 180, 50);
        require(response.find(" ok ") != std::string::npos, "empty-ocr-is-not-a-component-failure");
        response = request("ocr2", 240, 130, 480, 80);
        std::istringstream tokens(response); std::string reply, number, state, time, encoded; tokens >> reply >> number >> state >> time >> encoded;
        if (state != "ok") std::cerr << "Synthetic OCR error: " << response << "\n";
        else if (unhex(encoded).find(utf8(L"读取存档")) == std::string::npos) std::cerr << "Synthetic OCR result: " << unhex(encoded) << "\n";
        require(state == "ok" && unhex(encoded).find(utf8(L"读取存档")) != std::string::npos, "chinese-ocr");
        response = request("save-name", 270, 340, 350, 45);
        tokens.clear(); tokens.str(response); tokens >> reply >> number >> state >> time >> encoded;
        auto nickname = unhex(encoded);
        require(state == "ok" && nickname.find("\"light\"") != std::string::npos && nickname.find("\"dark\"") != std::string::npos
            && nickname.find(utf8(L"测试角色")) != std::string::npos && nickname.find(utf8(L"通关")) == std::string::npos, "save-name-yellow-filter");
        auto firstSnapshot = id;
        // 遮挡本夹具，再验证背景截图仍来自其窗口而非覆盖者。
        auto cover = CreateWindowW(L"BqtjScriptFixtureRoot", L"合成遮挡", WS_OVERLAPPEDWINDOW, 40, 40, 990, 660, nullptr, nullptr, instance, nullptr);
        ShowWindow(cover, SW_SHOWNOACTIVATE); UpdateWindow(cover); pump();
        response = request("ocr2", 240, 130, 480, 80);
        tokens.clear(); tokens.str(response); tokens >> reply >> number >> state >> time >> encoded;
        require(state == "ok" && unhex(encoded).find(utf8(L"助理")) != std::string::npos, "covered-ocr");
        auto snapshot = id;
        auto pixels = request("pixels", 400, 250, 40, 40, snapshot);
        tokens.clear(); tokens.str(pixels); std::string raw; tokens >> reply >> number >> state >> time >> encoded >> raw;
        require(state == "ok" && raw.size() == 40 * 40 * 6, "pixels");
        // 从本次合成截图构造BMP模板，检验真正的Maa模板匹配入口。
        auto data = unhex(raw);
        auto bmp = bitmap(data, 40, 40);
        response = request("image", 380, 230, 90, 90, 0, hex(bmp));
        tokens.clear(); tokens.str(response); tokens >> reply >> number >> state >> time >> encoded;
        require(state == "ok" && unhex(encoded).find("\"box\"") != std::string::npos, "template-match");
        // 不重复使用无纹理棋盘验证特征；文字图案提供足够非重复角点，检查新入口真实命中。
        response = request("pixels", 260, 140, 240, 50);
        tokens.clear(); tokens.str(response); tokens >> reply >> number >> state >> time >> encoded >> raw;
        require(state == "ok", "feature-source"); auto feature_bmp = bitmap(unhex(raw), 240, 50);
        auto feature_snapshot = id;
        response = request("feature-image", 240, 130, 300, 90, feature_snapshot, hex(feature_bmp));
        tokens.clear(); tokens.str(response); tokens >> reply >> number >> state >> time >> encoded;
        require(state == "ok" && unhex(encoded).find("\"box\"") != std::string::npos, "feature-match");
        POINT before = {}, after = {}; bool cursor_available = GetCursorPos(&before) != FALSE;
        bool headless = argc == 3 && std::wstring(argv[2]) == L"--headless";
        require(cursor_available || headless, "cursor-unavailable"); auto foreground = GetForegroundWindow();
        response = request("click", 410, 260, 0, 0); pump(); if (cursor_available) require(GetCursorPos(&after) != FALSE, "cursor-unavailable");
        require(response.find(" ok ") != std::string::npos && clicks == 1 && (!cursor_available || (before.x == after.x && before.y == after.y))
            && foreground == GetForegroundWindow(), "background-input");
        auto moves_before = moves;
        // 数字样式只改识别图像，蓝色未满计数仍返回真实数字，不被当成绿色完成。
        response = request("quota-counter", 270, 390, 150, 50);
        tokens.clear(); tokens.str(response); tokens >> reply >> number >> state >> time >> encoded;
        require(state == "ok" && unhex(encoded).find("26/27") != std::string::npos, "blue-quota-counter");
        response = request("hover", 150, 480, 0, 0); pump();
        require(response.find(" ok ") != std::string::npos && moves > moves_before && clicks == 1, "background-hover-no-click");
        // 系统光标可在窗口外；离开事件之后，截图必须重新维持本目标的后台悬停。
        SendMessageW(game, WM_MOUSELEAVE, 0, 0); moves_before = moves;
        response = request("pixels", 800, 420, 1, 1);
        tokens.clear(); tokens.str(response); tokens >> reply >> number >> state >> time >> encoded >> raw;
        auto hover_pixel = unhex(raw);
        require(state == "ok" && moves > moves_before && hover_pixel.size() == 3
            && (unsigned char)hover_pixel[1] == 210, "hover-refreshed-before-background-capture");
        auto key_start = GetTickCount64();
        response = request("key", 'D', 0, 120, 0); pump();
        require(response.find(" ok ") != std::string::npos && key_downs == 1 && key_ups == 1 && held_key == 0
            && GetTickCount64() - key_start >= 120, "key-pulse-released");
        // 下一步走位已结束悬停租用，后续截图不能把旧弹层重新唤起。
        SendMessageW(game, WM_MOUSELEAVE, 0, 0); moves_before = moves;
        response = request("pixels", 800, 420, 1, 1);
        tokens.clear(); tokens.str(response); tokens >> reply >> number >> state >> time >> encoded >> raw;
        require(state == "ok" && moves == moves_before && (unsigned char)unhex(raw)[1] == 20, "movement-clears-hover-lease");
        require(request("keys", 'W', 'D', 120, 0).find(" ok ") != std::string::npos && diagonal_seen
            && key_downs == 3 && key_ups == 3 && held_mask == 0, "diagonal-overlap-and-release");
        require(request("keys", 'W', 'S', 120, 0).find("coordinates") != std::string::npos && key_downs == 3, "opposite-keys-rejected");
        require(request("key", 'X', 0, 120, 0).find("coordinates") != std::string::npos && key_downs == 3, "non-movement-key-rejected");
        // 两个账号等价的独立Flash子窗口共享同一工作进程；消息只能落在本次绑定的窗口。
        auto secondRoot = CreateWindowW(L"BqtjScriptFixtureRoot", L"第二个虚构账号", WS_OVERLAPPEDWINDOW, 90, 90, 990, 660, nullptr, nullptr, instance, nullptr);
        auto firstGame = game;
        auto secondGame = CreateWindowW(L"MacromediaFlashPlayerActiveX", L"", WS_CHILD | WS_VISIBLE, 0, 0, 950, 600, secondRoot, nullptr, instance, nullptr);
        ShowWindow(secondRoot, SW_SHOWNOACTIVATE); pump();
        auto firstDowns = window_keys[firstGame].down;
        game = secondGame;
        response = request("pixels", 800, 420, 1, 1);
        tokens.clear(); tokens.str(response); tokens >> reply >> number >> state >> time >> encoded >> raw;
        require(state == "ok" && (unsigned char)unhex(raw)[1] == 20, "hover-does-not-leak-to-another-window");
        require(request("keys", 'S', 'A', 50, 0).find(" ok ") != std::string::npos, "second-window-key-pulse");
        require(window_keys[firstGame].down == firstDowns && window_keys[secondGame].down == 2 && window_keys[secondGame].up == 2, "two-window-key-isolation");
        require(request("battle-ai", 'N', 0, 50, 0).find(" ok ") != std::string::npos
            && window_keys[secondGame].down == 3 && window_keys[secondGame].up == 3
            && window_keys[firstGame].down == firstDowns, "ai-toggle-released-and-isolated");
        require(request("key", 'N', 0, 50, 0).find("coordinates") != std::string::npos, "n-is-not-movement");
        require(request("battle-ai", 'D', 0, 50, 0).find("coordinates") != std::string::npos, "ai-command-only-n");
        require(request("battle-ai-rise", 'N', 'W', 150, 0).find(" ok ") != std::string::npos
            && ai_rise_seen && n_released_while_rising && held_mask == 0
            && window_keys[secondGame].down == 5 && window_keys[secondGame].up == 5
            && window_keys[firstGame].down == firstDowns, "ai-and-rise-overlap-release-and-isolation");
        require(request("battle-ai-rise", 'N', 'D', 150, 0).find("coordinates") != std::string::npos, "ai-rise-only-w");
        game = firstGame;
        DestroyWindow(secondRoot);
        if (cursor_available) { GetCursorPos(&after); require(before.x == after.x && before.y == after.y, "hover-key-do-not-move-cursor"); }
        require(foreground == GetForegroundWindow(), "hover-key-no-activation");
        require(request("ocr", 0, 0, 20, 20, 0, "", GetCurrentProcessId() + 100000).find("target-changed") != std::string::npos, "identity");
        require(request("pixels", 0, 0, 10, 10, firstSnapshot).find("snapshot") != std::string::npos, "old-snapshot");
        ShowWindow(root, SW_MINIMIZE); pump();
        require(request("ocr", 0, 0, 20, 20).find("target-hidden") != std::string::npos, "minimized");
        ShowWindow(root, SW_SHOWNOACTIVATE); pump();
        SetWindowPos(game, nullptr, 0, 0, 900, 600, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        require(request("ocr", 0, 0, 20, 20).find("target-size") != std::string::npos, "resized");
        SetWindowPos(game, nullptr, 0, 0, 950, 600, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        require(request("release", 0, 0, 0, 0).find(" ok ") != std::string::npos, "release");
        CloseHandle(inputWrite); inputWrite = nullptr;
        auto start = GetTickCount64();
        while (WaitForSingleObject(process.hProcess, 0) != WAIT_OBJECT_0 && GetTickCount64() - start < 5000) { pump(); Sleep(10); }
        DWORD exitCode; GetExitCodeProcess(process.hProcess, &exitCode); require(exitCode == 0, "worker-exit");
        DestroyWindow(cover);
        std::cout << "PASS: native shared OCR, covered capture, template matching, background input, two-window key isolation, identity, stale frame, minimized/resize pause and exit\n";
        if (!cursor_available) std::cout << "NOTE: system cursor assertion unavailable on this non-interactive desktop; own-window input assertions passed\n";
        result = 0;
    } catch (const std::exception& error) { std::cerr << "FAIL: " << error.what() << "\n"; }
    if (inputWrite) CloseHandle(inputWrite);
    if (WaitForSingleObject(process.hProcess, 2000) != WAIT_OBJECT_0) TerminateProcess(process.hProcess, 1);
    CloseHandle(outputRead); CloseHandle(process.hThread); CloseHandle(process.hProcess); DestroyWindow(root);
    return result;
}
