"""portal-api 客户端测试（docs/API_SPEC.md 3.1/3.3/3.4/3.5）。"""

import unittest

from _path import SDK_DIR, FakeResponse, FakeSession, load_fixture  # noqa: F401
from ncu_sdk.config import HEADER_DEVICE_INFO, HEADER_ID_TOKEN, HEADER_TERMINAL_INFO
from ncu_sdk.errors import PermissionError_
from ncu_sdk.portal import PortalClient


def make_client():
    return PortalClient(token="eyJ-test-token")


class TestPortalHeaders(unittest.TestCase):
    def test_required_headers_sent(self):
        """3.1：三个头必须随请求发出。"""
        session = FakeSession()
        client = PortalClient(token="eyJ-test-token", session=session)
        client.get("v1/calendar/share/schedule/getCalendar")
        h = session.last_headers
        self.assertEqual(h.get(HEADER_ID_TOKEN), "eyJ-test-token")
        self.assertEqual(h.get(HEADER_DEVICE_INFO), "PC")
        self.assertEqual(h.get(HEADER_TERMINAL_INFO), "PC")

    def test_no_token_raises_permission(self):
        """3.3：未带 token -> {"code": -1, "message": "没有访问权限01"}。"""
        session = FakeSession().on(
            "GET", "getSection", FakeResponse(load_fixture("portal_no_token.json"))
        )
        client = PortalClient(token=None, session=session)
        with self.assertRaises(PermissionError_):
            client.get_section()


class TestPortalEndpoints(unittest.TestCase):
    def test_get_section(self):
        """3.4 getSection。"""
        session = FakeSession().on(
            "GET", "getSection", FakeResponse(load_fixture("portal_getSection.json"))
        )
        client = PortalClient(token="t", session=session)
        sections = client.get_section("2026-09-03")
        self.assertEqual(len(sections), 13)
        self.assertEqual(sections[4].label, "五")
        self.assertEqual(sections[4].start_time, "11:30")
        # 请求参数
        self.assertIn("2026-09-03", session.requests[0]["params"]["startDate"])

    def test_get_events_shape(self):
        """3.3：data.schedule[date].calendarList[] -> CalendarEvent。"""
        session = FakeSession().on(
            "GET", "getEvents", FakeResponse(load_fixture("portal_getEvents.json"))
        )
        client = PortalClient(token="t", session=session)
        events = client.get_events("2026-09-04", "2026-09-06", req_type="WeekView")
        self.assertEqual(len(events), 2)
        first = events[0]
        self.assertEqual(first.title, "学术报告：大模型推理优化")
        self.assertEqual(first.date, "2026-09-04")
        self.assertEqual(first.calendar_name, "学术活动")
        # 按日期升序
        self.assertEqual(events[0].date, "2026-09-04")
        self.assertEqual(events[1].date, "2026-09-05")

    def test_get_week_of_teaching_minus_one(self):
        """3.5：-1 表示未开学。"""
        session = FakeSession().on(
            "GET", "getWeekOfTeaching", FakeResponse(load_fixture("portal_getWeekOfTeaching.json"))
        )
        client = PortalClient(token="t", session=session)
        tw = client.get_week_of_teaching("2026-08-31")
        self.assertEqual(tw.semester, "1")
        self.assertEqual(tw.week, -1)
        self.assertFalse(tw.started)


if __name__ == "__main__":
    unittest.main()
