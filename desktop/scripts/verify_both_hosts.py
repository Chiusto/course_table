"""对最新 Debug 编译产物跑双宿主嵌入断言，验证 watcher 修复后 wallpaper/bottom 仍正常。

用法: python verify_both_hosts.py
退出码 0 = 两种宿主模式都通过；1 = 任一失败。
"""
import json
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).parent.parent
EXE = ROOT / "NcuCourseTable.Desktop" / "bin" / "Debug" / "net8.0-windows" / "NcuCourseTable.Desktop.exe"
SAMPLE = ROOT / "sample_data" / "courses.json"
HERE = Path(__file__).parent


def run_mode(host_arg, out):
    data_dir = Path(tempfile.mkdtemp(prefix="ncu_embed_"))
    shutil.copyfile(SAMPLE, data_dir / "courses.json")
    cmd = [str(EXE), "--data", str(data_dir)]
    if host_arg:
        cmd += ["--host", host_arg]
    proc = subprocess.Popen(cmd)
    try:
        # WPF 启动 + 资源加载 + 桌面宿主挂载需要 3~5s，等稳定后再断言
        time.sleep(5.0)
        result = subprocess.run(
            [sys.executable, str(HERE / "verify_embed.py"), host_arg or "bottom"],
            capture_output=True, text=True, timeout=30,
        )
        out["stdout"] = result.stdout.strip()
        out["returncode"] = result.returncode
        try:
            out["assert"] = json.loads(result.stdout)
        except Exception:
            out["assert"] = None
        return result.returncode == 0
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=8)
        except subprocess.TimeoutExpired:
            proc.kill()
        shutil.rmtree(data_dir, ignore_errors=True)


def main():
    if not EXE.exists():
        print(json.dumps({"error": f"exe not found: {EXE}"}))
        sys.exit(1)

    out = {"exe": str(EXE)}
    ok_wall = run_mode("wallpaper", out.setdefault("wallpaper", {}))
    time.sleep(1.0)
    ok_bot = run_mode("bottom", out.setdefault("bottom", {}))

    out["ok"] = ok_wall and ok_bot
    print(json.dumps(out, ensure_ascii=False, indent=1))
    sys.exit(0 if out["ok"] else 1)


if __name__ == "__main__":
    main()
