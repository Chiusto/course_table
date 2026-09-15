"""ncu_sdk.config —— 常量与运行配置。

所有 URL、Header 名、参数规则都直接取自 docs/API_SPEC.md，对应章节见注释。
这些常量是唯一需要随学校系统升级而修改的地方（尤其是 gmsstu 混淆路径）。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date

# ============================================================ 第 2 节：CAS
CAS_BASE = "https://cas.ncu.edu.cn:8443/cas"
CAS_LOGIN = f"{CAS_BASE}/login"
CAS_PUBLIC_KEY = f"{CAS_BASE}/jwt/publicKey"

# ================================================= 第 3 节：融合门户 portal-api
PORTAL_HOME = "https://my.ncu.edu.cn/"
PORTAL_MAIN = "https://my.ncu.edu.cn/main.html"
PORTAL_API_BASE = "https://my.ncu.edu.cn/portal-api/"

# 3.1 必须携带的三个头；实测 Authorization: Bearer 无效
HEADER_ID_TOKEN = "x-id-token"
HEADER_DEVICE_INFO = "x-device-info"
HEADER_TERMINAL_INFO = "x-terminal-info"
DEVICE_PC = "PC"

# 3.2 端点清单（相对 PORTAL_API_BASE）
EP_GET_EVENTS = "v1/calendar/share/schedule/getEvents"
EP_GET_SECTION = "v1/calendar/share/schedule/getSection"
EP_GET_WEEK_OF_TEACHING = "v1/calendar/share/schedule/getWeekOfTeaching"
EP_GET_SCHEDULE = "v1/calendar/share/schedule/getSchedule"
EP_GET_CALENDAR = "v1/calendar/share/schedule/getCalendar"
EP_GET_PERSONAL_CALENDAR = "v1/calendar/share/schedule/getPersonlCalendar"  # 注意原文拼写 Personl
EP_SUBSCRIBE_PAGE = "v1/calendar/share/subscribe/page"

# getEvents 的 reqType 取值
REQ_TYPE_WEEK = "WeekView"
REQ_TYPE_MONTH = "MonthView"

# ============================================ 第 4 节：研究生系统 gmsstu
GMSSTU_BASE = "https://gmsstu.ncu.edu.cn"
GMSSTU_INDEX = f"{GMSSTU_BASE}/index"

# 4.1：前缀 TXlIZWFydFdpbGxHb09u = Base64("MyHeartWillGoOn")。
# 注意：整段路径并非静态——前 32 位 Hex 固定，后 80 位 Hex 随登录会话轮换，
# 静态值仅作快路径；失效时 GmsstuClient 会自动按会话重新发现端点（见 gmsstu.py）。
GMSSTU_OBFUSCATED_PATH = (
    "TXlIZWFydFdpbGxHb09u"
    "01E8D4410B737A813BDBF14D3F0C8535E67EAD08C30ABFDF401A9480D9F0633"
    "D7D7F4E773764713FA35A5BB072274B5270ABA6F0FAA9D541"
)
GMSSTU_KB_URL = f"{GMSSTU_BASE}/{GMSSTU_OBFUSCATED_PATH}"

# 4.2 请求参数
GMSSTU_KBLX = "xs"  # 固定值：学生课表
GMSSTU_COOKIE = "cn_com_southsoft_gmis_stu"  # 认证是否成功的判定 Cookie

# 4.2：jcid 1-13 → 第一至第十三节；z1-z7 → 周一至周日
SECTION_MIN, SECTION_MAX = 1, 13
WEEKDAY_KEYS = ("z1", "z2", "z3", "z4", "z5", "z6", "z7")

DEFAULT_TIMEOUT = 15
DEFAULT_USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/124.0 Safari/537.36"
)


@dataclass
class Settings:
    """SDK 运行配置。除 db_path 外一般无需修改。"""

    # ---- 网络 ----
    timeout: float = DEFAULT_TIMEOUT
    user_agent: str = DEFAULT_USER_AGENT
    verify_ssl: bool = True  # 校园网证书异常时可置 False（会打印警告）
    proxies: dict[str, str] | None = None

    # ---- 端点（系统升级时只改这些字段）----
    cas_base: str = CAS_BASE
    portal_api_base: str = PORTAL_API_BASE
    portal_home: str = PORTAL_HOME
    gmsstu_base: str = GMSSTU_BASE
    gmsstu_obfuscated_path: str = GMSSTU_OBFUSCATED_PATH
    gmsstu_index: str = GMSSTU_INDEX

    # ---- 存储 ----
    db_path: str = "ncu_schedule.db"
    section_cache_days: int = 30  # 6：getSection 结果本地缓存天数

    # ---- 教学周 ----
    # termcode -> 该学期第一周周一（ISO 日期）。留空则只能靠 portal 校准或手动传入。
    # 例：{"202620271": "2026-08-31"}
    term_start_dates: dict[str, str] = field(default_factory=dict)
    # 6：portal 的 getWeekOfTeaching 仅用于校准，默认开启
    use_portal_week_calibration: bool = True

    @property
    def gmsstu_kb_url(self) -> str:
        return f"{self.gmsstu_base.rstrip('/')}/{self.gmsstu_obfuscated_path.lstrip('/')}"


# ============================================================ termcode 规则
def make_termcode(academic_start_year: int, term: int) -> str:
    """学年起始年 + 学期号 -> termcode。

    docs/API_SPEC.md 4.2：202620271 = 2026-2027 学年 第 1 学期，第 2 学期为 202620272。
    """
    if term not in (1, 2):
        raise ValueError("term 只能是 1 或 2")
    return f"{academic_start_year}{academic_start_year + 1}{term}"


def parse_termcode(termcode: str) -> tuple[int, int]:
    """termcode -> (学年起始年, 学期号)。如 "202620271" -> (2026, 1)。"""
    termcode = str(termcode).strip()
    if len(termcode) != 9 or not termcode.isdigit():
        raise ValueError(f"termcode 形如 202620271，收到：{termcode!r}")
    return int(termcode[:4]), int(termcode[8])


def termcode_of(day: date) -> str:
    """按日期推算所属 termcode。9 月~次年 1 月为第 1 学期，2~8 月为第 2 学期。"""
    if day.month >= 9:
        return make_termcode(day.year, 1)
    if day.month == 1:
        return make_termcode(day.year - 1, 1)
    return make_termcode(day.year - 1, 2)
