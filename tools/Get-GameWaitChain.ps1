param([Parameter(Mandatory)][int[]]$ProcessIds)
$ErrorActionPreference = 'Stop'

# 只读取指定游戏进程的线程等待关系，不读取内存、网页内容或凭据。
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GameWaitChain {
    [DllImport("advapi32.dll", SetLastError=true)]
    public static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);
    [DllImport("advapi32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetThreadWaitChain(IntPtr session, IntPtr context, uint flags,
        uint threadId, ref uint count, IntPtr nodes, [MarshalAs(UnmanagedType.Bool)] out bool cycle);
    [DllImport("advapi32.dll")]
    public static extern void CloseThreadWaitChainSession(IntPtr session);
}
'@
$session = [GameWaitChain]::OpenThreadWaitChainSession(0, [IntPtr]::Zero)
if ($session -eq [IntPtr]::Zero) { throw 'Cannot open wait-chain session' }
# WAITCHAIN_NODE_INFO 的联合体包含 128 个 WCHAR，按 8 字节对齐后节点大小为 280。
$buffer = [Runtime.InteropServices.Marshal]::AllocHGlobal(16 * 280)
try {
    foreach ($targetId in $ProcessIds) {
        $process = Get-Process -Id $targetId -ErrorAction SilentlyContinue
        if (!$process) {
            [pscustomobject]@{ProcessId=$targetId; State='Exited'}
            continue
        }
        foreach ($thread in $process.Threads) {
            [uint32]$count = 16
            $cycle = $false
            $ok = [GameWaitChain]::GetThreadWaitChain($session, [IntPtr]::Zero, 1,
                [uint32]$thread.Id, [ref]$count, $buffer, [ref]$cycle)
            if (!$ok) {
                [pscustomobject]@{ProcessId=$targetId; ThreadId=$thread.Id; Error=[Runtime.InteropServices.Marshal]::GetLastWin32Error()}
                continue
            }
            $nodes = for ($i=0; $i -lt $count; $i++) {
                $offset = $i * 280
                $kind = [Runtime.InteropServices.Marshal]::ReadInt32($buffer, $offset)
                $status = [Runtime.InteropServices.Marshal]::ReadInt32($buffer, $offset+4)
                $node = @{Type=$kind; Status=$status}
                # WctThreadType=8；不输出锁对象名称，以避免记录不必要的用户信息。
                if ($kind -eq 8) {
                    $node.ProcessId = [Runtime.InteropServices.Marshal]::ReadInt32($buffer, $offset+8)
                    $node.ThreadId = [Runtime.InteropServices.Marshal]::ReadInt32($buffer, $offset+12)
                }
                $node
            }
            [pscustomobject]@{ProcessId=$targetId;ThreadId=$thread.Id;Cycle=$cycle;Nodes=@($nodes)}
        }
    }
} finally {
    [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)
    [GameWaitChain]::CloseThreadWaitChainSession($session)
}
