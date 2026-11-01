# 状態別のトレイアイコン。
# AdGuard のロゴは商標のためリポジトリに含めず、インストールした AdGuard Home の管理画面から取得して使う
# （Windows 版と同じ形: 公式の盾に、AdGuard for Windows と区別するための「家」バッジを右下に重ねる）。
# 取得できない間は、独自に描いた盾を使う。
#   緑とチェック: 実行中 / 橙: 新しい版あり / 灰と×: 停止中・保護オフ / 赤と×: サービス未登録
import os
import re
import urllib.request

from PyQt6.QtCore import QPointF, QRectF, Qt
from PyQt6.QtGui import QColor, QIcon, QPainter, QPainterPath, QPen, QPixmap, QPolygonF

from .system import CONFIG_DIR

LOGO_FILE = os.path.join(CONFIG_DIR, "logo.svg")
LOGO_PATH = "assets/safari-pinned-tab.svg"   # 管理画面にある、16x16 の座標の盾とチェックの図形

COLORS = {
    "running": "#68bc71",
    "update": "#f0a030",
    "stopped": "#8a8f98",
    "unknown": "#d9534f",
}
MARKS = {"running": "check", "update": "check", "stopped": "cross", "unknown": "cross"}

_cache = {}
_logo = {"stamp": None, "shapes": None}


def fetch_logo(base_url):
    """管理画面からロゴを取得して保存する。新しく保存できたら True"""
    if os.path.exists(LOGO_FILE) or not base_url:
        return False
    try:
        with urllib.request.urlopen(base_url.rstrip("/") + "/" + LOGO_PATH, timeout=5) as r:
            data = r.read(200_000).decode("utf-8", "replace")
    except Exception:
        return False
    if "<svg" not in data or len(_parse_shapes(data)) < 2:
        return False
    os.makedirs(CONFIG_DIR, mode=0o700, exist_ok=True)
    tmp = LOGO_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(data)
    os.replace(tmp, LOGO_FILE)
    _cache.clear()
    return True


def _parse_shapes(svg):
    """M / L / C / Z（絶対座標）だけの簡単な SVG パスを読む。M ごとに別の図形にする（1 つ目が盾、2 つ目がチェック）"""
    m = re.search(r'\sd="([^"]+)"', svg)
    if not m:
        return []
    shapes, cur, cmd, nums = [], None, " ", []

    def flush():
        if cur is None:
            return
        if cmd == "L":
            for i in range(0, len(nums) - 1, 2):
                cur.lineTo(nums[i], nums[i + 1])
        elif cmd == "C":
            for i in range(0, len(nums) - 5, 6):
                cur.cubicTo(*nums[i:i + 6])
        nums.clear()

    for tk in re.findall(r"[MLCZmlcz]|-?\d*\.?\d+(?:e-?\d+)?", m.group(1)):
        if tk.isalpha():
            c = tk.upper()
            if cmd != "M":
                flush()
            if c == "Z":
                if cur is not None:
                    cur.closeSubpath()
            elif c == "M":
                nums.clear()
            cmd = c
            continue
        nums.append(float(tk))
        if cmd == "M" and len(nums) == 2:
            cur = QPainterPath()
            cur.setFillRule(Qt.FillRule.WindingFill)
            cur.moveTo(nums[0], nums[1])
            shapes.append(cur)
            nums.clear()
            cmd = "L"
    flush()
    return shapes


def _logo_shapes():
    try:
        stamp = os.path.getmtime(LOGO_FILE)
    except OSError:
        stamp = None
    if stamp != _logo["stamp"]:
        _logo["stamp"] = stamp
        _logo["shapes"] = None
        if stamp is not None:
            try:
                with open(LOGO_FILE, encoding="utf-8") as f:
                    shapes = _parse_shapes(f.read())
                _logo["shapes"] = shapes if len(shapes) >= 2 else None
            except OSError:
                pass
    return _logo["shapes"]


def _shield(size):
    s = size
    p = QPainterPath()
    p.moveTo(s * 0.5, s * 0.06)
    p.cubicTo(s * 0.68, s * 0.14, s * 0.82, s * 0.16, s * 0.9, s * 0.16)
    p.cubicTo(s * 0.9, s * 0.58, s * 0.78, s * 0.8, s * 0.5, s * 0.95)
    p.cubicTo(s * 0.22, s * 0.8, s * 0.1, s * 0.58, s * 0.1, s * 0.16)
    p.cubicTo(s * 0.18, s * 0.16, s * 0.32, s * 0.14, s * 0.5, s * 0.06)
    p.closeSubpath()
    return p


def _draw_fallback(g, state, size):
    g.setPen(Qt.PenStyle.NoPen)
    g.setBrush(QColor(COLORS[state]))
    g.drawPath(_shield(size))
    pen = QPen(QColor("white"), max(1.5, size * 0.1), Qt.PenStyle.SolidLine,
               Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin)
    g.setPen(pen)
    s = size
    if MARKS[state] == "check":
        g.drawPolyline([QPointF(s * 0.32, s * 0.5), QPointF(s * 0.45, s * 0.63), QPointF(s * 0.69, s * 0.36)])
    else:
        r = QRectF(s * 0.35, s * 0.33, s * 0.3, s * 0.3)
        g.drawLine(r.topLeft(), r.bottomRight())
        g.drawLine(r.topRight(), r.bottomLeft())


def _draw_logo(g, shapes, state, size):
    """Windows 版の RenderIcon と同じ描き方"""
    # 本家 AdGuard のアイコンに合わせ、上下左右に約 4% の余白を取る
    pad = max(1 if size >= 16 else 0, round(size * 0.04))
    k = (size - pad * 2) / 16
    gray = state in ("stopped", "unknown")
    g.save()
    g.translate(pad, pad)
    g.scale(k, k)
    g.setPen(Qt.PenStyle.NoPen)
    g.setBrush(QColor("#8e8e8e" if gray else "#67b279"))
    g.drawPath(shapes[0])
    g.setBrush(QColor("white"))
    g.drawPath(shapes[1])
    g.restore()

    # 家バッジ（通知領域の小さいサイズは見分けやすさを優先して大きめ）
    badge = QColor({"update": "#f08c1a", "unknown": "#d63c3c"}.get(state, "#263245"))
    d = size * (0.50 if size <= 32 else 0.40)
    bx = by = size - pad - d
    ring = max(1.0, size / 16)
    g.setBrush(QColor("white"))
    g.drawEllipse(QRectF(bx - ring, by - ring, d + ring, d + ring))
    g.setBrush(badge)
    g.drawEllipse(QRectF(bx, by, d - ring * 0.2, d - ring * 0.2))
    cx, cy, u = bx + d / 2, by + d / 2, d / 10
    g.setBrush(QColor("white"))
    g.drawPolygon(QPolygonF([
        QPointF(cx, cy - 3.6 * u),            # 屋根の頂点
        QPointF(cx + 3.7 * u, cy - 0.2 * u), QPointF(cx + 2.5 * u, cy - 0.2 * u),
        QPointF(cx + 2.5 * u, cy + 3.2 * u), QPointF(cx - 2.5 * u, cy + 3.2 * u),
        QPointF(cx - 2.5 * u, cy - 0.2 * u), QPointF(cx - 3.7 * u, cy - 0.2 * u)]))
    if size >= 24:
        g.setBrush(badge)
        g.drawRect(QRectF(cx - 0.8 * u, cy + 1.0 * u, 1.6 * u, 2.2 * u))


def pixmap(state, size):
    pm = QPixmap(size, size)
    pm.fill(Qt.GlobalColor.transparent)
    g = QPainter(pm)
    g.setRenderHint(QPainter.RenderHint.Antialiasing)
    shapes = _logo_shapes()
    if shapes:
        _draw_logo(g, shapes, state, size)
    else:
        _draw_fallback(g, state, size)
    g.end()
    return pm


def icon(state):
    if state not in _cache:
        ic = QIcon()
        for size in (16, 22, 24, 32, 48, 64, 128):
            ic.addPixmap(pixmap(state, size))
        _cache[state] = ic
    return _cache[state]
