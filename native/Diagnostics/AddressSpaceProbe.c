/* 只保留虚拟地址、不提交大块物理内存；验证 Flash 宿主在 x64 Windows 上可用高位地址。 */
#include <windows.h>
#include <stdio.h>

int main(void)
{
    BOOL wow64 = FALSE;
    IsWow64Process(GetCurrentProcess(), &wow64);
    if (!wow64) { puts("SKIP: high address space requires x64 Windows"); return 0; }
    void *block = VirtualAlloc((void *)(uintptr_t)0x90000000u, 65536, MEM_RESERVE, PAGE_READWRITE);
    printf("x86 address above 2GB: %s\n", block ? "PASS" : "FAIL");
    if (block) VirtualFree(block, 0, MEM_RELEASE);
    return block ? 0 : 1;
}
