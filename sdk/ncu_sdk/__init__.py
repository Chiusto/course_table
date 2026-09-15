"""ncu_sdk —— 南昌大学课程表 Python SDK。

依据 docs/API_SPEC.md 实现，包含两套系统的客户端：

    融合门户（第 3 节）：PortalClient   节次表 / 教学周 / 日程 / 日历订阅
    研究生系统（第 4 节）：GmsstuClient  培养方案真课表（按学期、按节次）
    统一认证（第 2 节）：CASClient

推荐从 NCUClient 进入（登录 -> 同步 -> 查询/导出）：

    from ncu_sdk import NCUClient
    client = NCUClient(username="学号", password="密码")
    client.sync("202620271")
    for course in client.week_schedule("202620271", week=3):
        print(course.describe(client.sections()))

凭据建议通过环境变量 NCU_USERNAME / NCU_PASSWORD 提供，不要写进代码。
"""

from __future__ import annotations

from .cas import CASClient
from .config import (
    GMSSTU_COOKIE,
    PORTAL_API_BASE,
    Settings,
    make_termcode,
    parse_termcode,
    termcode_of,
)
from .demo import sample_courses, sample_rows, sample_sections
from .errors import (
    ApiError,
    AuthError,
    ConfigError,
    EndpointOutdatedError,
    GmsstuSessionError,
    LoginError,
    NCUSDKError,
    ParseError,
    PermissionError_,
    TokenNotFoundError,
)
from .gmsstu import GmsstuClient
from .ics import build_ics
from .models import CalendarEvent, Course, Section, TeachingWeek
from .parser import parse_cell, parse_mc, parse_rows, parse_sections_payload
from .portal import PortalClient
from .schedule import NCUClient, SyncResult
from .storage import ScheduleStore
from .weeks import WeekSpec

__version__ = "0.1.0"

__all__ = [
    # 顶层入口
    "NCUClient",
    "SyncResult",
    # 客户端
    "CASClient",
    "PortalClient",
    "GmsstuClient",
    # 模型与解析
    "Course",
    "Section",
    "CalendarEvent",
    "TeachingWeek",
    "WeekSpec",
    "parse_cell",
    "parse_mc",
    "parse_rows",
    "parse_sections_payload",
    # 存储与导出
    "ScheduleStore",
    "build_ics",
    # 配置
    "Settings",
    "PORTAL_API_BASE",
    "GMSSTU_COOKIE",
    "make_termcode",
    "parse_termcode",
    "termcode_of",
    # 样例
    "sample_courses",
    "sample_rows",
    "sample_sections",
    # 异常
    "NCUSDKError",
    "AuthError",
    "LoginError",
    "TokenNotFoundError",
    "GmsstuSessionError",
    "ApiError",
    "PermissionError_",
    "EndpointOutdatedError",
    "ParseError",
    "ConfigError",
    "__version__",
]
