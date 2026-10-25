"""脱离父进程启动一个 GUI 程序（用于自动化测试）。

为什么不用 Start-Process / cmd start：
  某些沙箱环境会按 Job Object 回收子进程，且中文路径 + 空格在命令行里容易截断。
  这里直接 CreateProcess 带 DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP，
  参数走 argv 数组，不经过 shell 解析。

用法：
  python launch-detached.py "C:\\path\\to\\app.exe" [工作目录]
"""

import subprocess
import sys

DETACHED_PROCESS = 0x00000008
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_BREAKAWAY_FROM_JOB = 0x01000000


def main() -> int:
    if len(sys.argv) < 2:
        print("用法: launch-detached.py <exe> [cwd]")
        return 2

    exe = sys.argv[1]
    cwd = sys.argv[2] if len(sys.argv) > 2 else None

    flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
    try:
        p = subprocess.Popen([exe], cwd=cwd, creationflags=flags | CREATE_BREAKAWAY_FROM_JOB)
    except OSError:
        # 进程已在某个不允许 breakaway 的 Job 里时，去掉该标志重试
        p = subprocess.Popen([exe], cwd=cwd, creationflags=flags)

    print(f"已启动 pid={p.pid}  {exe}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
