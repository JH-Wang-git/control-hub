"""列出所有音频会话：进程名、状态、峰值。用来判断某个播放器现在是在播放还是暂停。"""
import sys
from pycaw.pycaw import AudioUtilities

def sessions():
    out = []
    for s in AudioUtilities.GetAllSessions():
        try:
            name = s.Process.name() if s.Process else "(system)"
        except Exception:
            name = "(?)"
        meter = s.SimpleAudioVolume
        state = "?"
        peak = 0.0
        try:
            from pycaw.pycaw import IAudioMeterInformation
            mi = s._ctl.QueryInterface(IAudioMeterInformation)
            peak = mi.GetPeakValue()
        except Exception:
            pass
        try:
            # AudioSessionState: 0 inactive, 1 active, 2 expired
            state = s._ctl.GetState()
        except Exception:
            pass
        vol = 0.0
        try:
            vol = meter.GetMasterVolume()
        except Exception:
            pass
        out.append((name, state, peak, vol))
    return out

if __name__ == "__main__":
    filt = sys.argv[1].lower() if len(sys.argv) > 1 else None
    rows = [r for r in sessions() if not filt or filt in r[0].lower()]
    if not rows:
        print("no session")
    for name, state, peak, vol in rows:
        print(f"{name:20s} state={state} peak={peak:.4f} vol={vol:.2f}")
