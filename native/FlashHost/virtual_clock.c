#include "virtual_clock.h"

static SRWLOCK g_clock_lock = SRWLOCK_INIT;
static LARGE_INTEGER g_frequency;
static LONGLONG g_real_qpc_base;
static double g_virtual_qpc_base;
static double g_virtual_tick_base;
static double g_speed = 1.0;
static BOOL g_initialized;

/* 必须在独占锁内调用；把同一段真实时间同时结算到毫秒钟和高精度时钟。 */
static void settle_to(LONGLONG real_qpc_now)
{
    LONGLONG real_delta = real_qpc_now - g_real_qpc_base;
    g_virtual_qpc_base += (double)real_delta * g_speed;
    g_virtual_tick_base +=
        ((double)real_delta * 1000.0 / (double)g_frequency.QuadPart) * g_speed;
    g_real_qpc_base = real_qpc_now;
}

BOOL virtual_clock_initialize(void)
{
    AcquireSRWLockExclusive(&g_clock_lock);
    if (!g_initialized)
    {
        LARGE_INTEGER now;
        if (!QueryPerformanceFrequency(&g_frequency)
            || g_frequency.QuadPart <= 0
            || !QueryPerformanceCounter(&now))
        {
            ReleaseSRWLockExclusive(&g_clock_lock);
            return FALSE;
        }

        g_real_qpc_base = now.QuadPart;
        g_virtual_qpc_base = (double)now.QuadPart;
        g_virtual_tick_base = (double)GetTickCount();
        g_initialized = TRUE;
    }
    ReleaseSRWLockExclusive(&g_clock_lock);
    return TRUE;
}

BOOL virtual_clock_set_speed(double speed)
{
    if (speed < 0.01 || speed > 100.0 || !virtual_clock_initialize())
    {
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }

    AcquireSRWLockExclusive(&g_clock_lock);
    LARGE_INTEGER now;
    if (!QueryPerformanceCounter(&now))
    {
        ReleaseSRWLockExclusive(&g_clock_lock);
        return FALSE;
    }

    /* 先连续结算再换斜率；不需要回原速，也不会制造 300 ms 的停顿。 */
    settle_to(now.QuadPart);
    g_speed = speed;
    ReleaseSRWLockExclusive(&g_clock_lock);
    return TRUE;
}

DWORD WINAPI virtual_clock_get_tick_count(void)
{
    if (!virtual_clock_initialize())
    {
        return GetTickCount();
    }

    AcquireSRWLockShared(&g_clock_lock);
    LARGE_INTEGER now;
    if (!QueryPerformanceCounter(&now))
    {
        ReleaseSRWLockShared(&g_clock_lock);
        return GetTickCount();
    }
    double elapsed_ms =
        (double)(now.QuadPart - g_real_qpc_base) * 1000.0
        / (double)g_frequency.QuadPart;
    DWORD result = (DWORD)(ULONGLONG)(g_virtual_tick_base + elapsed_ms * g_speed);
    ReleaseSRWLockShared(&g_clock_lock);
    return result;
}

BOOL WINAPI virtual_clock_query_performance_counter(LARGE_INTEGER *value)
{
    if (value == NULL || !virtual_clock_initialize())
    {
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }

    AcquireSRWLockShared(&g_clock_lock);
    LARGE_INTEGER now;
    BOOL succeeded = QueryPerformanceCounter(&now);
    if (succeeded)
    {
        double virtual_now = g_virtual_qpc_base
            + (double)(now.QuadPart - g_real_qpc_base) * g_speed;
        value->QuadPart = (LONGLONG)virtual_now;
    }
    ReleaseSRWLockShared(&g_clock_lock);
    return succeeded;
}
