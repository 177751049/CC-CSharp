import subprocess, time, datetime, os, sys

LOG = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "cc-net-sampler.log")
APP_MARK = "\u96f7\u8fbe"  # 雷达

def run(args, timeout=20):
    try:
        p = subprocess.run(args, capture_output=True, timeout=timeout, shell=False)
        out = p.stdout or b""
        for enc in ("gbk", "utf-8"):
            try:
                return out.decode(enc)
            except Exception:
                continue
        return out.decode("gbk", "replace")
    except Exception as e:
        return f"<err {e}>"

def ping(target):
    t = run(["ping", "-n", "4", "-w", "2000", target])
    loss = None; avg = None
    for line in t.splitlines():
        if "\u4e22\u5931" in line or "loss" in line:
            # 丢失 = 0 (0% 丢失)
            for tok in line.replace("%", " % ").split():
                pass
            import re
            m = re.search(r"(\d+)%", line)
            if m: loss = int(m.group(1))
        if "\u5e73\u5747" in line or "Average" in line:
            import re
            m = re.search(r"(\d+)ms", line)
            if m: avg = int(m.group(1))
    return avg, loss

def count_app():
    t = run(["tasklist", "/FO", "CSV", "/NH"], timeout=25)
    n = 0
    for line in t.splitlines():
        if APP_MARK in line or "CC\u96f7\u8fbe" in line:
            n += 1
    return n

def arp_lines():
    t = run(["arp", "-a"], timeout=15)
    keep = []
    for line in t.splitlines():
        s = line.strip()
        if s.startswith("192.168.6.1 ") or s.startswith("192.168.6.100 ") or s.startswith("192.168.6.254 "):
            keep.append(" ".join(s.split()))
    return "; ".join(keep)

def tcp_retrans():
    t = run(["netstat", "-s", "-p", "tcp"], timeout=15)
    keep = []
    for line in t.splitlines():
        if "\u91cd\u4f20" in line or "Retrans" in line or "\u5931\u8d25" in line:
            keep.append(" ".join(line.split()))
    return " | ".join(keep[:4])

with open(LOG, "a", encoding="utf-8") as f:
    f.write(f"\n===== sampler start {datetime.datetime.now():%Y-%m-%d %H:%M:%S} =====\n")
    f.flush()
    end = time.time() + 20 * 60
    while time.time() < end:
        ts = datetime.datetime.now().strftime("%H:%M:%S")
        app = count_app()
        g_avg, g_loss = ping("192.168.6.1")
        b_avg, b_loss = ping("baidu.com")
        arp = arp_lines()
        rt = tcp_retrans()
        line = (f"[{ts}] app={app} | gw 192.168.6.1 avg={g_avg}ms loss={g_loss}% "
                f"| baidu avg={b_avg}ms loss={b_loss}% | arp: {arp} | tcp: {rt}")
        f.write(line + "\n"); f.flush()
        time.sleep(3)
    f.write(f"===== sampler end {datetime.datetime.now():%Y-%m-%d %H:%M:%S} =====\n")
print("done", LOG)
