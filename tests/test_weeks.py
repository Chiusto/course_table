"""周次解析测试（docs/API_SPEC.md 4.2 中的周次写法 + 常见教务写法）。"""

import unittest

from _path import SDK_DIR  # noqa: F401  (设置 sys.path)
from ncu_sdk.errors import ParseError
from ncu_sdk.weeks import EVEN, ODD, WeekSpec


class TestWeekSpec(unittest.TestCase):
    def test_continuous_range(self):
        w = WeekSpec.parse("1-11周")
        self.assertEqual((w.start, w.end), (1, 11))
        self.assertTrue(w.contains(1) and w.contains(11) and w.contains(7))
        self.assertFalse(w.contains(12) and w.contains(0))

    def test_single_week(self):
        # 文档样例：工程伦理[6-6周]
        w = WeekSpec.parse("6-6周")
        self.assertTrue(w.contains(6))
        self.assertFalse(w.contains(5) or w.contains(7))
        self.assertEqual(w.raw, "6-6周")

    def test_document_samples(self):
        # 4.2 实测课表中的所有周次写法
        for raw in ("5-15周", "1-11周", "1-16周", "6-6周"):
            spec = WeekSpec.parse(raw)
            self.assertTrue(spec.contains(int(raw.split("-")[0])))

    def test_parity(self):
        odd = WeekSpec.parse("1-9周(单)")
        even = WeekSpec.parse("2-10周(双)")
        self.assertIs(odd.parity, ODD)
        self.assertIs(even.parity, EVEN)
        self.assertTrue(odd.contains(1) and not odd.contains(2))
        self.assertTrue(even.contains(10) and not even.contains(9))

    def test_multi_ranges(self):
        w = WeekSpec.parse("1-8周,10-12周")
        self.assertTrue(w.contains(1) and w.contains(8) and w.contains(12))
        self.assertFalse(w.contains(9))

    def test_discrete(self):
        w = WeekSpec.parse("1,3,5周")
        self.assertEqual(w.weeks(limit=6), [1, 3, 5])

    def test_expand(self):
        w = WeekSpec.parse("1-3周")
        self.assertEqual(list(w), [1, 2, 3])

    def test_invalid(self):
        with self.assertRaises(ParseError):
            WeekSpec.parse("")
        with self.assertRaises(ParseError):
            WeekSpec.parse("无周次信息")

    def test_str_preserves_raw(self):
        self.assertEqual(str(WeekSpec.parse("5-15周")), "5-15周")


if __name__ == "__main__":
    unittest.main()
