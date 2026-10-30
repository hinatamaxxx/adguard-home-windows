# 表示の言語: ロケールが日本語なら日本語、それ以外は英語。
# 文言は T("日本語", "English") のように、使う場所に両方を並べて書く（Windows 版の L.T と同じ）。
import locale
import os


def _detect_japanese():
    # 環境変数 ADGUARD_TRAY_LANG（ja / en）で指定もできる（確認やスクリーンショット用）
    forced = os.environ.get("ADGUARD_TRAY_LANG")
    if forced in ("ja", "en"):
        return forced == "ja"
    for name in ("LC_ALL", "LC_MESSAGES", "LANGUAGE", "LANG"):
        value = os.environ.get(name)
        if value:
            return value.lower().startswith("ja")
    try:
        return (locale.getlocale()[0] or "").lower().startswith("ja")
    except Exception:
        return False


JA = _detect_japanese()


def T(ja, en):
    return ja if JA else en


def units(n, ja_suffix, en_unit):
    """「10 分」/「10 minutes」"""
    return f"{n}{ja_suffix}" if JA else f"{n} {en_unit}{'' if n == 1 else 's'}"


def format_duration(ms):
    s = ms / 1000
    if s >= 86400 and s % 86400 == 0:
        return units(int(s // 86400), " 日間", "day")
    if s >= 3600:
        return units(round(s / 3600), " 時間", "hour")
    if s >= 60:
        return units(-(-int(s) // 60), " 分", "minute")
    return units(max(1, -(-int(s) // 1)), " 秒", "second")


def ago(seconds):
    """「3 分前」/「3 minutes ago」"""
    if seconds < 60:
        return T("たった今", "just now")
    if seconds < 3600:
        n, ja, en = int(seconds // 60), " 分前", "minute"
    elif seconds < 86400:
        n, ja, en = int(seconds // 3600), " 時間前", "hour"
    else:
        n, ja, en = int(seconds // 86400), " 日前", "day"
    return f"{n}{ja}" if JA else f"{n} {en}{'' if n == 1 else 's'} ago"
