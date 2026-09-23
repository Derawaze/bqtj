/* 直接复用生产命令读取线程，验证空闲命令管道不会锁住 CRT 全流刷新。 */
#include "../FlashHost/native_flash_host.c"

int main(void)
{
    HANDLE reader = CreateThread(NULL, 0, command_reader, NULL, 0, NULL);
    if (reader == NULL) return 2;
    /* 父进程保持 stdin 管道打开且不写命令，让读取线程进入阻塞读取。 */
    Sleep(300);
    fflush(NULL);
    puts("flush-completed");
    fflush(stdout);
    /* 探针只拥有当前进程，退出时由操作系统回收仍等待输入的线程。 */
    ExitProcess(0);
}
