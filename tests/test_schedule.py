"""顶层编排测试：登录会话校验、同步落库、按周/按日查询、教学周判定（docs 第 6 节）。"""

import unittest
from datetime import date

from _path import SDK_DIR, FakeResponse, FakeSession, load_fixture  # noqa: F401
from ncu_sdk.config import GMSSTU_COOKIE, Settings, make_termcode, parse_termcode, termcode_of
from ncu_sdk.demo import sample_courses
from ncu_sdk.errors import GmsstuSessionError
from ncu_sdk.gmsstu import GmsstuClient
from ncu_sdk.schedule import NCUClient
from ncu_sdk.storage import ScheduleStore


def gmsstu_rows_response():
    """把 demo 样例 rows 包成 gmsstu 响应。"""
    from ncu_sdk.demo import sample_rows
    return FakeResponse({"rows": sample_rows()})


def build_client(gmsstu_session: FakeSession, term_start: dict | None = None,
                 with_gmsstu_cookie: bool = True) -> NCUClient:
    settings = Settings(term_start_dates=term_start or {})
    store = ScheduleStore(":memory:")
    cookie_name = GMSSTU_COOKIE if with_gmsstu_cookie else None
    gms = GmsstuClient(settings=settings, session=gmsstu_session)
    client = NCUClient(username="u", password="p", settings=settings, store=store,
                       session=gmsstu_session)
    client.gmsstu = gms
    client.portal.token = "eyJ-test"  # 关闭门户自动登录
    client._logged_in = True
    return client


class TestTermcode(unittest.TestCase):
    def test_make_and_parse(self):
        self.assertEqual(make_termcode(2026, 1), "202620271")
        self.assertEqual(make_termcode(2026, 2), "202620272")
        self.assertEqual(parse_termcode("202620271"), (2026, 1))

    def test_termcode_of(self):
        # 4.2：202620271 = 2026-2027 第 1 学期
        self.assertEqual(termcode_of(date(2026, 9, 4)), "202620271")
        self.assertEqual(termcode_of(date(2026, 12, 1)), "202620271")
        self.assertEqual(termcode_of(date(2027, 1, 15)), "202620271")   # 1 月属上学期
        self.assertEqual(termcode_of(date(2027, 3, 1)), "202620272")


class TestGmsstuClient(unittest.TestCase):
    def test_require_cookie(self):
        """4.1：无 cn_com_southsoft_gmis_stu Cookie 时报错。"""
        session = FakeSession()  # 无 gmsstu Cookie
        client = GmsstuClient(session=session)
        with self.assertRaises(GmsstuSessionError):
            client.fetch_rows("202620271")

    def test_post_payload_and_parse(self):
        """4.2：POST 表单 kblx=xs & termcode，X-Requested-With 头。"""
        session = FakeSession(cookie_name=GMSSTU_COOKIE).on("POST", "TXlIZWFydFdpbGxHb09u",
                                                            gmsstu_rows_response())
        client = GmsstuClient(session=session)
        courses = client.fetch_courses("202620271")
        self.assertEqual(len(courses), 8)
        req = session.requests[0]
        self.assertEqual(req["data"], {"kblx": "xs", "termcode": "202620271"})
        self.assertEqual(req["headers"]["X-Requested-With"], "XMLHttpRequest")

    def test_discover_kb_url_when_static_path_rotated(self):
        """混淆路径随会话轮换：静态路径 500 -> 自动发现新端点并成功拉表。

        实测（2026-09）：路径 = 固定前缀 + 固定 hex 头 + 每次登录轮换的 hex 尾，
        旧路径任何新会话都返回 {"code":500,"msg":"服务器异常"}。
        """
        index_html = (
            '<a href="/TXlIZWFydFdpbGxHb09uAAAA1111">首页</a>'
            '<iframe src="/TXlIZWFydFdpbGxHb09uBBBB2222"></iframe>'
        )
        kb_page_html = (
            "function GetList() { $.ajax({ type: 'post', dataType: 'json', "
            "url: '/TXlIZWFydFdpbGxHb09uCCCC3333', "
            "data: { 'kblx': 'xs', 'termcode': '202620271' }, "
            "success: function (data) {} }); }"
        )
        session = (
            FakeSession(cookie_name=GMSSTU_COOKIE)
            .on("POST", "01E8D441", FakeResponse(payload={"code": 500, "msg": "服务器异常"}))
            .on("GET", "/index", FakeResponse(text=index_html, url="https://gmsstu.ncu.edu.cn/index"))
            .on("GET", "BBBB2222", FakeResponse(text=kb_page_html, url="https://gmsstu.ncu.edu.cn/x"))
            .on("POST", "CCCC3333", gmsstu_rows_response())
        )
        client = GmsstuClient(session=session)
        courses = client.fetch_courses("202620271")
        self.assertEqual(len(courses), 8)
        # 最后一次 POST 应指向发现的新端点
        self.assertIn("CCCC3333", session.requests[-1]["url"])
        self.assertEqual(session.requests[-1]["data"]["termcode"], "202620271")


class TestSync(unittest.TestCase):
    def test_sync_replaces_and_counts(self):
        session = FakeSession(cookie_name=GMSSTU_COOKIE).on("POST", "TXlIZWFydFdpbGxHb09u",
                                                            gmsstu_rows_response())
        client = build_client(session, term_start={"202620271": "2026-08-31"})
        result = client.sync("202620271")
        self.assertEqual(result.courses, 8)
        self.assertEqual(len(client.store.get_courses(termcode="202620271")), 8)


class TestQuery(unittest.TestCase):
    def setUp(self):
        session = FakeSession(cookie_name=GMSSTU_COOKIE).on("POST", "TXlIZWFydFdpbGxHb09u",
                                                            gmsstu_rows_response())
        self.client = build_client(session, term_start={"202620271": "2026-08-31"})
        self.client.store.replace_term_courses("202620271", sample_courses())

    def test_week_schedule(self):
        # 第 10 周：机器学习(1-11周) 有；组合数学(5-15周) 有
        courses = self.client.week_schedule("202620271", week=10)
        names = {c.name for c in courses}
        self.assertIn("机器学习", names)
        self.assertIn("组合数学", names)

    def test_week_schedule_monday(self):
        monday = self.client.day_schedule("202620271", week=10, weekday=1)
        self.assertEqual(len(monday), 1)
        self.assertEqual(monday[0].name, "机器学习")
        self.assertEqual((monday[0].start_section, monday[0].end_section), (6, 8))

    def test_today_schedule_weekday(self):
        # 2026-09-03 是第 1 周周四：自然辩证法（1-8 周）在当天上
        week, courses = self.client.today_schedule("202620271", day=date(2026, 9, 3))
        self.assertEqual(week, 1)
        names = {c.name for c in courses}
        self.assertIn("自然辩证法", names)
        self.assertNotIn("组合数学", names)  # 5-15 周，第 1 周不上

    def test_resolve_week_from_term_start(self):
        """无 portal 时用第一周周一推算（8/31 开学 -> 第 1 周）。"""
        self.assertEqual(self.client.resolve_week("202620271", date(2026, 9, 4)), 1)
        self.assertEqual(self.client.resolve_week("202620271", date(2026, 9, 14)), 3)
        # 未开学
        self.assertEqual(self.client.resolve_week("202620271", date(2026, 8, 1)), -1)

    def test_date_of_week(self):
        self.assertEqual(self.client.date_of_week("202620271", 1, weekday=1), date(2026, 8, 31))
        self.assertEqual(self.client.date_of_week("202620271", 3, weekday=5), date(2026, 9, 18))


if __name__ == "__main__":
    unittest.main()
