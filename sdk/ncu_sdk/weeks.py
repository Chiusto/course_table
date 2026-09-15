"""ncu_sdk.weeks —— 周次表达式解析。

docs/API_SPEC.md 4.2 中每个单元格形如：
    "组合数学[5-15周] 幸玮[前湖北校区研究生院316]"
方括号内即周次表达式。实际教务数据里还会出现这些写法，这里一并兼容：

    "1-11周"        连续区间
    "6-6周"         单周（区间两端相同）
    "1-9周(单)"     奇数周
    "2-10周(双)"    偶数周
    "1,3,5周"       离散周
    "1-8周,10-12周" 多段
    "3周"           单周简写
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Iterator

from .errors import ParseError

# 匹配一段区间：12-16、12～16、12 周
_RANGE_RE = re.compile(r"(\d{1,2})\s*(?:[-~—－]\s*(\d{1,2}))?\s*周?")
# 单/双周标记（可能出现中英文括号）
_SINGLE_RE = re.compile(r"[（(]\s*单\s*[)）]")
_DOUBLE_RE = re.compile(r"[（(]\s*双\s*[)）]")

ODD = "odd"
EVEN = "even"


@dataclass(frozen=True)
class WeekSpec:
    """一个课程的周次规则。

    Attributes:
        ranges: 闭区间列表，如 ((1, 11), (14, 16))
        parity: None（每周）/ "odd"（单周）/ "even"（双周）
        raw: 原始字符串，便于回溯与展示
    """

    ranges: tuple[tuple[int, int], ...] = ()
    parity: str | None = None
    raw: str = ""

    # ---------------------------------------------------------------- 解析
    @classmethod
    def parse(cls, text: str) -> "WeekSpec":
        """解析周次表达式；无法识别时抛 ParseError。"""
        if text is None:
            raise ParseError("周次表达式为空")
        raw = str(text).strip()
        if not raw:
            raise ParseError("周次表达式为空")

        parity = ODD if _SINGLE_RE.search(raw) else (EVEN if _DOUBLE_RE.search(raw) else None)
        ranges: list[tuple[int, int]] = []
        for start, end in _RANGE_RE.findall(raw):
            s = int(start)
            e = int(end) if end else s
            if e < s:
                s, e = e, s
            ranges.append((s, e))
        if not ranges:
            raise ParseError(f"无法解析周次表达式：{raw!r}")
        return cls(ranges=tuple(ranges), parity=parity, raw=raw)

    # ---------------------------------------------------------------- 查询
    def contains(self, week: int) -> bool:
        """判断第 week 周是否上课。"""
        if not any(s <= week <= e for s, e in self.ranges):
            return False
        if self.parity == ODD and week % 2 == 0:
            return False
        if self.parity == EVEN and week % 2 == 1:
            return False
        return True

    def weeks(self, limit: int = 30) -> list[int]:
        """展开为周次列表（用于导出日历、统计课时）。"""
        return [w for w in range(1, limit + 1) if self.contains(w)]

    def __iter__(self) -> Iterator[int]:
        return iter(self.weeks())

    def __contains__(self, week: object) -> bool:
        return isinstance(week, int) and self.contains(week)

    @property
    def start(self) -> int:
        return min(s for s, _ in self.ranges)

    @property
    def end(self) -> int:
        return max(e for _, e in self.ranges)

    def __str__(self) -> str:  # 展示用，保留原始写法
        return self.raw or "、".join(f"{s}-{e}" for s, e in self.ranges)
