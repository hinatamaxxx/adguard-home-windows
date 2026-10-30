# 状態別のトレイアイコン（独自に描いた盾。AdGuard のロゴは商標のため使わない）。
# 緑とチェック: 実行中 / 橙: 新しい版あり / 灰と×: 停止中・保護オフ / 赤と×: サービス未登録
from PyQt6.QtCore import QPointF, QRectF, Qt
from PyQt6.QtGui import QColor, QIcon, QPainter, QPainterPath, QPen, QPixmap

COLORS = {
    "running": "#68bc71",
    "update": "#f0a030",
    "stopped": "#8a8f98",
    "unknown": "#d9534f",
}
MARKS = {"running": "check", "update": "check", "stopped": "cross", "unknown": "cross"}

_cache = {}


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


def pixmap(state, size):
    pm = QPixmap(size, size)
    pm.fill(Qt.GlobalColor.transparent)
    g = QPainter(pm)
    g.setRenderHint(QPainter.RenderHint.Antialiasing)
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
    g.end()
    return pm


def icon(state):
    if state not in _cache:
        ic = QIcon()
        for size in (16, 22, 24, 32, 48, 64, 128):
            ic.addPixmap(pixmap(state, size))
        _cache[state] = ic
    return _cache[state]
