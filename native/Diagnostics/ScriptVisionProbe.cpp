#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <algorithm>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

/* 合成窗口仅测试自己的Flash同名子窗口；不枚举、截图或点击用户的游戏。 */
static int clicks = 0;
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
    SelectObject(dc, previous); DeleteObject(font);
}
static LRESULT CALLBACK procedure(HWND window, UINT message, WPARAM word, LPARAM value) {
    if (message == WM_PAINT) { PAINTSTRUCT state; auto dc = BeginPaint(window, &state); paint(window, dc); EndPaint(window, &state); return 0; }
    if (message == WM_PRINT || message == WM_PRINTCLIENT) { paint(window, (HDC)word); return 0; }
    if (message == WM_LBUTTONDOWN) { clicks++; return 0; }
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
        auto response = request("ocr2", 240, 130, 480, 80);
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
        BITMAPFILEHEADER fileHeader = {}; BITMAPINFOHEADER imageHeader = {};
        fileHeader.bfType = 0x4d42; fileHeader.bfOffBits = sizeof(fileHeader) + sizeof(imageHeader); fileHeader.bfSize = fileHeader.bfOffBits + (DWORD)data.size();
        imageHeader.biSize = sizeof(imageHeader); imageHeader.biWidth = 40; imageHeader.biHeight = -40; imageHeader.biPlanes = 1; imageHeader.biBitCount = 24;
        std::vector<unsigned char> bmp(fileHeader.bfSize);
        std::copy_n((unsigned char*)&fileHeader, sizeof(fileHeader), bmp.data());
        std::copy_n((unsigned char*)&imageHeader, sizeof(imageHeader), bmp.data() + sizeof(fileHeader));
        std::copy(data.begin(), data.end(), bmp.begin() + fileHeader.bfOffBits);
        response = request("image", 380, 230, 90, 90, 0, hex(bmp));
        tokens.clear(); tokens.str(response); tokens >> reply >> number >> state >> time >> encoded;
        require(state == "ok" && unhex(encoded).find("\"box\"") != std::string::npos, "template-match");
        POINT before = {}, after = {}; bool cursor_available = GetCursorPos(&before) != FALSE;
        bool headless = argc == 3 && std::wstring(argv[2]) == L"--headless";
        require(cursor_available || headless, "cursor-unavailable"); auto foreground = GetForegroundWindow();
        response = request("click", 410, 260, 0, 0); pump(); if (cursor_available) require(GetCursorPos(&after) != FALSE, "cursor-unavailable");
        require(response.find(" ok ") != std::string::npos && clicks == 1 && (!cursor_available || (before.x == after.x && before.y == after.y))
            && foreground == GetForegroundWindow(), "background-input");
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
        std::cout << "PASS: native shared OCR, covered capture, template matching, background input, identity, stale frame, minimized/resize pause and exit\n";
        if (!cursor_available) std::cout << "NOTE: system cursor assertion unavailable on this non-interactive desktop; own-window input assertions passed\n";
        result = 0;
    } catch (const std::exception& error) { std::cerr << "FAIL: " << error.what() << "\n"; }
    if (inputWrite) CloseHandle(inputWrite);
    if (WaitForSingleObject(process.hProcess, 2000) != WAIT_OBJECT_0) TerminateProcess(process.hProcess, 1);
    CloseHandle(outputRead); CloseHandle(process.hThread); CloseHandle(process.hProcess); DestroyWindow(root);
    return result;
}
