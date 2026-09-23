#include <windows.h>
#include <stdio.h>

typedef void (__stdcall *initialize_speedhack_t)(float);
typedef DWORD (WINAPI *get_tick_count_t)(void);
typedef BOOL (WINAPI *query_performance_counter_t)(LARGE_INTEGER *);

/*
 * 绕过依赖易语言宿主的 GameSpeed.dll，直接验证底层 Speedhack.dll 导出。
 * 通过真实等待 2 秒后的计时器增量，判断 2 倍速钩子是否实际生效。
 */
int wmain(int argc, wchar_t **argv)
{
    if (argc != 2)
    {
        fwprintf(stderr, L"usage: speedhack_probe.exe <Speedhack.dll>\n");
        return 2;
    }

    HMODULE module = LoadLibraryW(argv[1]);
    if (module == NULL)
    {
        fwprintf(stderr, L"LoadLibraryW failed: %lu\n", GetLastError());
        return 3;
    }

    union
    {
        FARPROC source;
        initialize_speedhack_t initialize;
    } export_pointer;
    export_pointer.source = GetProcAddress(module, "InitializeSpeedhack");
    if (export_pointer.initialize == NULL)
    {
        fprintf(stderr, "GetProcAddress failed: %lu\n", GetLastError());
        return 4;
    }

    union
    {
        FARPROC source;
        get_tick_count_t invoke;
    } accelerated_tick_count;
    union
    {
        FARPROC source;
        query_performance_counter_t invoke;
    } accelerated_performance_counter;
    accelerated_tick_count.source = GetProcAddress(module, "speedhackversion_GetTickCount");
    accelerated_performance_counter.source =
        GetProcAddress(module, "speedhackversion_QueryPerformanceCounter");
    if (accelerated_tick_count.invoke == NULL || accelerated_performance_counter.invoke == NULL)
    {
        fprintf(stderr, "speedhack timer exports are unavailable\n");
        return 5;
    }

    /*
     * 这两个“导出函数”实际是可写的函数指针槽。GameSpeed.dll 会先填充它们；
     * 绕过包装层时必须由宿主提供未被加速钩子替换的原始计时函数。
     */
    FARPROC *real_get_tick_count = (FARPROC *)GetProcAddress(module, "realGetTickCount");
    FARPROC *real_query_performance_counter =
        (FARPROC *)GetProcAddress(module, "realQueryPerformanceCounter");
    HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
    if (real_get_tick_count == NULL || real_query_performance_counter == NULL || kernel32 == NULL)
    {
        fprintf(stderr, "speedhack timer slots are unavailable\n");
        return 6;
    }

    *real_get_tick_count = GetProcAddress(kernel32, "GetTickCount");
    *real_query_performance_counter = GetProcAddress(kernel32, "QueryPerformanceCounter");
    if (*real_get_tick_count == NULL || *real_query_performance_counter == NULL)
    {
        fprintf(stderr, "original timer functions are unavailable\n");
        return 7;
    }

    LARGE_INTEGER frequency;
    LARGE_INTEGER counter_before;
    LARGE_INTEGER counter_after;
    QueryPerformanceFrequency(&frequency);
    export_pointer.initialize(2.0f);
    accelerated_performance_counter.invoke(&counter_before);
    DWORD ticks_before = accelerated_tick_count.invoke();
    Sleep(2000);

    DWORD ticks_after = accelerated_tick_count.invoke();
    accelerated_performance_counter.invoke(&counter_after);
    double qpc_seconds = (double)(counter_after.QuadPart - counter_before.QuadPart)
        / (double)frequency.QuadPart;
    printf(
        "speed=2.0 tick-delta=%lu qpc-seconds=%.3f\n",
        (unsigned long)(ticks_after - ticks_before),
        qpc_seconds);

    /* 再次初始化应只更新倍率和基准点，供菜单在运行中反复切换倍率。 */
    export_pointer.initialize(1.5f);
    accelerated_performance_counter.invoke(&counter_before);
    ticks_before = accelerated_tick_count.invoke();
    Sleep(2000);
    ticks_after = accelerated_tick_count.invoke();
    accelerated_performance_counter.invoke(&counter_after);
    qpc_seconds = (double)(counter_after.QuadPart - counter_before.QuadPart)
        / (double)frequency.QuadPart;
    printf(
        "speed=1.5 tick-delta=%lu qpc-seconds=%.3f\n",
        (unsigned long)(ticks_after - ticks_before),
        qpc_seconds);
    return 0;
}
