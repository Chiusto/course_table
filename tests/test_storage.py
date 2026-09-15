"""SQLite 存储测试（docs/API_SPEC.md 6：按 termcode-weekday-sections 索引、节次表缓存）。"""

import time
import unittest

from _path import SDK_DIR  # noqa: F401
from ncu_sdk.demo import sample_courses, sample_sections
from ncu_sdk.storage import ScheduleStore, monday_of
from datetime import date


class TestScheduleStore(unittest.TestCase):
    def setUp(self):
        self.store = ScheduleStore(":memory:")
        self.courses = sample_courses()

    def tearDown(self):
        self.store.close()

    def test_replace_and_query_by_termcode(self):
        n = self.store.replace_term_courses("202620271", self.courses)
        self.assertEqual(n, len(self.courses))
        self.assertEqual(len(self.store.get_courses(termcode="202620271")), len(self.courses))
        self.assertEqual(self.store.get_courses(termcode="999999991"), [])

    def test_index_by_weekday(self):
        self.store.replace_term_courses("202620271", self.courses)
        wed = self.store.get_courses(termcode="202620271", weekday=3)
        self.assertEqual({c.name for c in wed},
                         {"高级计算机系统结构", "数据科学与工程", "最优化"})

    def test_index_by_week(self):
        """week 过滤走 gmsstu 周次规则：第 10 周有组合数学（5-15 周）。"""
        self.store.replace_term_courses("202620271", self.courses)
        week10 = self.store.get_courses(termcode="202620271", week=10)
        names = {c.name for c in week10}
        self.assertIn("组合数学", names)      # 5-15周
        self.assertIn("机器学习", names)      # 1-11周
        self.assertNotIn("自然辩证法", names)  # 1-8周

    def test_replace_is_idempotent(self):
        self.store.replace_term_courses("202620271", self.courses)
        self.store.replace_term_courses("202620271", self.courses)
        self.assertEqual(len(self.store.get_courses(termcode="202620271")), len(self.courses))

    def test_sections_cache(self):
        self.store.save_sections(sample_sections(), source="portal")
        sections = self.store.get_sections()
        self.assertEqual(len(sections), 13)
        self.assertEqual(sections[6].start_time, "14:00")

    def test_sections_expiry(self):
        """6：getSection 本地缓存，过期后返回空，促使上层重新拉取。"""
        self.store.save_sections(sample_sections(), source="portal")
        self.assertEqual(len(self.store.get_sections(max_age_days=30)), 13)
        self.assertEqual(self.store.get_sections(max_age_days=0), {})

    def test_meta(self):
        self.store.set_meta("portal_token", "eyJabc")
        self.assertEqual(self.store.get_meta("portal_token"), "eyJabc")
        self.store.set_json("start", {"202620271": "2026-08-31"})
        self.assertEqual(self.store.get_json("start"), {"202620271": "2026-08-31"})

    def test_monday_of(self):
        self.assertEqual(monday_of(date(2026, 9, 4)), date(2026, 8, 31))  # 周五 -> 周一


if __name__ == "__main__":
    unittest.main()
