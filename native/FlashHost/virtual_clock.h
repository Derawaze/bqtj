#ifndef BQTJ_VIRTUAL_CLOCK_H
#define BQTJ_VIRTUAL_CLOCK_H

#include <windows.h>

/*
 * Flash 专用连续虚拟时钟。
 * 切换倍率时先按旧倍率结算到“当前虚拟时间”，再从同一点继续，避免时间倒退或跳变。
 */
BOOL virtual_clock_initialize(void);
BOOL virtual_clock_set_speed(double speed);
DWORD WINAPI virtual_clock_get_tick_count(void);
BOOL WINAPI virtual_clock_query_performance_counter(LARGE_INTEGER *value);

#endif
