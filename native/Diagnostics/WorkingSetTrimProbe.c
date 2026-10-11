#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include <stdio.h>

/* 输出当前探针进程的私有提交量与工作集，不读取其他进程的内存。 */
static void report(const char *stage)
{
    PROCESS_MEMORY_COUNTERS_EX info = {0};
    info.cb = sizeof(info);
    if (!GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS *)&info, sizeof(info))) {
        printf("memory-query-failed=%lu\n", GetLastError());
        return;
    }
    printf("%s private=%.2fMiB working-set=%.2fMiB\n", stage,
           (double)info.PrivateUsage / 1048576.0, (double)info.WorkingSetSize / 1048576.0);
}

/* 仅分配 128MiB 虚构数据，比较修剪驻留页、重新访问原数据与实际释放的差别。 */
int main(void)
{
    const SIZE_T size = 128U * 1024U * 1024U;
    SYSTEM_INFO system;
    volatile BYTE *fixture;
    SIZE_T offset, sum = 0;
    setvbuf(stdout, NULL, _IONBF, 0);
    GetSystemInfo(&system);
    fixture = VirtualAlloc(NULL, size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!fixture) return 2;
    for (offset = 0; offset < size; offset += system.dwPageSize) fixture[offset] = 1;
    report("allocated-and-touched");
    if (!SetProcessWorkingSetSize(GetCurrentProcess(), (SIZE_T)-1, (SIZE_T)-1)) {
        printf("trim-failed=%lu\n", GetLastError());
        VirtualFree((void *)fixture, 0, MEM_RELEASE);
        return 2;
    }
    report("after-trim");
    /* 不重新分配：重新访问原来的每一页，验证对象仍占用地址与提交量。 */
    for (offset = 0; offset < size; offset += system.dwPageSize) sum += fixture[offset];
    report("after-retouch");
    printf("fixture-still-alive=%s pages=%lu\n", sum == size / system.dwPageSize ? "PASS" : "FAIL", (unsigned long)sum);
    VirtualFree((void *)fixture, 0, MEM_RELEASE);
    report("after-actual-release");
    return sum == size / system.dwPageSize ? 0 : 1;
}
