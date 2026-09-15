"""ncu_sdk.portal —— 融合门户 portal-api（docs/API_SPEC.md 第 3 节）。

必须携带的三个头（3.1）：x-id-token / x-device-info / x-terminal-info。
实测 Authorization: Bearer 无效。未带 token 时接口返回
{"code": -1, "message": "没有访问权限01"}，这里统一转成 PermissionError_。

注意 3.1 + 6 的取舍：getEvents 等日程接口只覆盖学术活动/订阅日历，
真正的培养方案课表在 gmsstu（见 gmsstu.py）；本模块还负责节次表与教学周校准。
"""

from __future__ import annotations

import re
from datetime import date
from typing import Any, Iterable

import requests

from .config import (
    DEVICE_PC,
    EP_GET_CALENDAR,
    EP_GET_EVENTS,
    EP_GET_PERSONAL_CALENDAR,
    EP_GET_SCHEDULE,
    EP_GET_SECTION,
    EP_GET_WEEK_OF_TEACHING,
    EP_SUBSCRIBE_PAGE,
    HEADER_DEVICE_INFO,
    HEADER_ID_TOKEN,
    HEADER_TERMINAL_INFO,
    REQ_TYPE_WEEK,
    Settings,
)
from .errors import ApiError, EndpointOutdatedError, PermissionError_, TokenNotFoundError
from .models import CalendarEvent, Section, TeachingWeek
from .parser import parse_sections_payload

# portal 前端通常把 JWT 写进 localStorage 的 token 键
_TOKEN_PATTERNS = (
    r'localStorage\.setItem\(\s*["\']token["\']\s*,\s*["\']([^"\']+)',
    r'["\']x-id-token["\']\s*[:=]\s*["\']([^"\']+)',
    r'["\']?token["\']?\s*[:=]\s*["\'](eyJ[A-Za-z0-9_\-.]+)',
    r'["\']idToken["\']\s*[:=]\s*["\']([^"\']+)',
)
_TOKEN_RE = [re.compile(p) for p in _TOKEN_PATTERNS]


class PortalClient:
    """portal-api 客户端。token 可从浏览器 localStorage 复制，也可由 CAS 登录换取。"""

    def __init__(
        self,
        token: str | None = None,
        settings: Settings | None = None,
        session: requests.Session | None = None,
    ) -> None:
        self.settings = settings or Settings()
        self.session = session or requests.Session()
        self.token = token

    # ---------------------------------------------------------------- 请求基石
    @property
    def headers(self) -> dict[str, str]:
        """3.1 要求的固定请求头。"""
        return {
            HEADER_ID_TOKEN: self.token or "",
            HEADER_DEVICE_INFO: DEVICE_PC,
            HEADER_TERMINAL_INFO: DEVICE_PC,
            "User-Agent": self.settings.user_agent,
        }

    def get(self, endpoint: str, params: dict | None = None) -> dict:
        """GET 并解包 {code, message, data}。code != 0 时抛异常。"""
        url = self.settings.portal_api_base.rstrip("/") + "/" + endpoint.lstrip("/")
        resp = self.session.get(
            url,
            params=params,
            headers=self.headers,
            timeout=self.settings.timeout,
            verify=self.settings.verify_ssl,
        )
        resp.raise_for_status()
        try:
            payload = resp.json()
        except ValueError as exc:
            raise EndpointOutdatedError(f"portal 接口返回非 JSON：{resp.text[:200]}") from exc

        code = payload.get("code")
        if code not in (0, "0"):
            message = payload.get("message") or "未知错误"
            if code == -1 or "没有访问权限" in message:
                raise PermissionError_(message, code=code, payload=payload)
            raise ApiError(message, code=code, payload=payload)
        return payload

    # ---------------------------------------------------------------- 登录换 token
    def login(self, username: str, password: str) -> str:
        """通过 CAS 登录门户并抽取 x-id-token。

        门户前端把 ticket 换成 JWT 后写入 localStorage，服务端不保证在 HTML 中
        回吐；因此这里依次尝试：Set-Cookie、HTML 内联脚本。都失败则抛
        TokenNotFoundError，调用方从浏览器复制 token 后手动传入。
        """
        from .cas import CASClient

        session = CASClient(self.settings, self.session).login_and_open(
            username, password, self.settings.portal_home
        )
        self.session = session
        self.token = self._extract_token_from_session()
        if not self.token:
            raise TokenNotFoundError(
                "未能自动获取 x-id-token。请在浏览器登录 my.ncu.edu.cn 后，"
                "从 localStorage.token 复制并传入 PortalClient(token=...)"
            )
        return self.token

    def _extract_token_from_session(self) -> str:
        for cookie in self.session.cookies:
            if cookie.name.lower() in ("token", "x-id-token", "id_token") and cookie.value.startswith("ey"):
                return cookie.value
        try:
            resp = self.session.get(
                self.settings.portal_home,
                timeout=self.settings.timeout,
                verify=self.settings.verify_ssl,
            )
            return self.extract_token(resp.text)
        except requests.RequestException:
            return ""

    @staticmethod
    def extract_token(html: str) -> str:
        """从门户 HTML 中抽取 JWT。"""
        for pattern in _TOKEN_RE:
            m = pattern.search(html or "")
            if m:
                return m.group(1)
        return ""

    # ---------------------------------------------------------------- 3.2 端点
    def get_events(
        self,
        start_date: str | date,
        end_date: str | date,
        req_type: str = REQ_TYPE_WEEK,
        calendar_id: str | None = None,
    ) -> list[CalendarEvent]:
        """3.3 周/月视图主查询。data.schedule 以日期为键，值为 calendarList。"""
        params = {
            "startDate": _as_str(start_date),
            "endDate": _as_str(end_date),
            "reqType": req_type,
        }
        if calendar_id:
            params["calendarId"] = calendar_id
        payload = self.get(EP_GET_EVENTS, params)
        schedule = (payload.get("data") or {}).get("schedule") or {}
        events: list[CalendarEvent] = []
        for day_key, day_data in schedule.items():
            items: Iterable[dict] = []
            if isinstance(day_data, dict):
                items = day_data.get("calendarList") or []
            elif isinstance(day_data, list):
                items = day_data
            for item in items:
                events.append(CalendarEvent.from_api(day_key, item))
        events.sort(key=lambda e: (e.date, e.start_time))
        return events

    def get_section(self, start_date: str | date | None = None) -> list[Section]:
        """3.4 节次时间表（如无缓存则应落库，见 storage.ScheduleStore）。"""
        params = {"startDate": _as_str(start_date or date.today())}
        return parse_sections_payload(self.get(EP_GET_SECTION, params))

    def get_week_of_teaching(self, today: str | date | Iterable[str | date] | None = None) -> TeachingWeek:
        """3.5 教学周/学期查询。date 为 -1 表示未开学。"""
        if today is None:
            value = _as_str(date.today())
        elif isinstance(today, (str, date)):
            value = _as_str(today)
        else:
            value = ",".join(_as_str(d) for d in today)  # 接口支持逗号分隔多个日期
        payload = self.get(EP_GET_WEEK_OF_TEACHING, {"today": value})
        data = (payload.get("data") or {}).get("data") or {}
        dates = data.get("date") or [-1]
        week = dates[0] if isinstance(dates, list) and dates else -1
        return TeachingWeek(
            semester=str(data.get("semester") or ""),
            week=_to_int(week, -1),
            raw_date=tuple(dates),
        )

    def get_schedule(self, schedule_id: str) -> dict[str, Any]:
        """单条日程详情。"""
        return (self.get(EP_GET_SCHEDULE, {"scheduleId": schedule_id}).get("data") or {})

    def get_calendar(self) -> list[dict]:
        """日历列表。"""
        data = self.get(EP_GET_CALENDAR).get("data")
        return _as_list(data)

    def get_personal_calendar(self) -> list[dict]:
        """个人日历（注意接口原词为 Personl）。"""
        data = self.get(EP_GET_PERSONAL_CALENDAR).get("data")
        return _as_list(data)

    def get_subscribed(self, page_type: str = "subscribed") -> list[dict]:
        """已订阅日历。"""
        data = self.get(EP_SUBSCRIBE_PAGE, {"pageType": page_type}).get("data")
        return _as_list(data)


def _as_str(value: date | str) -> str:
    return value.isoformat() if isinstance(value, date) else str(value)


def _to_int(value, default: int) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _as_list(data) -> list[dict]:
    if isinstance(data, list):
        return data
    if isinstance(data, dict):
        for key in ("list", "records", "rows", "data"):
            if isinstance(data.get(key), list):
                return data[key]
        return [data]
    return []
