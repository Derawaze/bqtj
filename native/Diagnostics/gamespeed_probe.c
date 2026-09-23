#include <windows.h>
#include <stdio.h>
#include <wchar.h>

typedef void (__stdcall *set_game_speed_t)(float);

/*
 * 最小化 GameSpeed.dll 崩溃复现：不创建浏览器、窗口或额外线程，
 * 只还原参考启动器的 Data 工作目录并调用命名导出 SetGameSpeed。
 */
int wmain(int argc, wchar_t **argv)
{
    if (argc != 2)
    {
        fwprintf(stderr, L"usage: gamespeed_probe.exe <GameSpeed.dll>\n");
        return 2;
    }

    wchar_t data_directory[MAX_PATH];
    wcsncpy(data_directory, argv[1], ARRAYSIZE(data_directory) - 1);
    data_directory[ARRAYSIZE(data_directory) - 1] = L'\0';
    wchar_t *separator = wcsrchr(data_directory, L'\\');
    if (separator == NULL)
    {
        return 3;
    }

    *separator = L'\0';
    separator = wcsrchr(data_directory, L'\\');
    if (separator == NULL)
    {
        return 4;
    }

    *separator = L'\0';
    if (!SetCurrentDirectoryW(data_directory))
    {
        fwprintf(stderr, L"SetCurrentDirectoryW failed: %lu\n", GetLastError());
        return 5;
    }

    HMODULE module = LoadLibraryW(argv[1]);
    if (module == NULL)
    {
        fwprintf(stderr, L"LoadLibraryW failed: %lu\n", GetLastError());
        return 6;
    }

    union
    {
        FARPROC source;
        set_game_speed_t set_speed;
    } export_pointer;
    export_pointer.source = GetProcAddress(module, "SetGameSpeed");
    if (export_pointer.set_speed == NULL)
    {
        fprintf(stderr, "GetProcAddress failed: %lu\n", GetLastError());
        return 7;
    }

    export_pointer.set_speed(2.0f);
    puts("speed-ok-returned");
    fflush(stdout);

    /* 保持进程存活，使异步钩子故障能被稳定捕获。 */
    Sleep(20000);
    puts("probe-stable");
    return 0;
}
