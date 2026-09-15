# 南昌大学课程表接口说明（my.ncu.edu.cn + gmsstu.ncu.edu.cn）

> 生成时间：2026-09-03 | 验证方式：真实校园账号（登录凭据仅通过 NCU_USERNAME / NCU_PASSWORD 环境变量注入，不入库）
> 适用：自研课程表 App / 小程序 / 桌面客户端

---

## 1. 总览

南昌大学课表分散在两个独立系统，切勿混用：

| 场景 | 入口 | 后端网关 | 认证 | 数据 |
|---|---|---|---|---|
| 日历/日程中心 ScheduleCenter | `https://my.ncu.edu.cn/main.html#/ScheduleCenter` | `https://my.ncu.edu.cn/portal-api/` | CAS JWT `x-id-token` | 学术活动、订阅日历 |
| 研究生真课表 ★ | `https://gmsstu.ncu.edu.cn/index` | `https://gmsstu.ncu.edu.cn/TXlIZWFydF...` | CAS SSO Cookie `cn_com_southsoft_gmis_stu` | 培养方案课表（按学期、按节次） |

> 做课程表软件必须接 gmsstu，ScheduleCenter 只能做节次表/教学周辅助。

---

## 2. 统一身份认证 CAS

```
GET https://my.ncu.edu.cn/main.html -> 302 -> https://cas.ncu.edu.cn:8443/cas/login?service=...
GET https://cas.ncu.edu.cn:8443/cas/jwt/publicKey  # 当前 encryptEnabled=false 明文即可
POST https://cas.ncu.edu.cn:8443/cas/login?service=...
  username=<学号>   # 由 NCU_USERNAME 环境变量注入
  password=<密码>   # 由 NCU_PASSWORD 环境变量注入
  execution=xxx  # 隐藏域
```
成功 302 到 `https://my.ncu.edu.cn/?ticket=ST-xxx`，前端换 JWT 写入 `localStorage token` 和 Cookie `TGC/SESSION/isLogin`。

---

## 3. 融合门户 ScheduleCenter（portal-api）

### 3.1 基础信息
- **BaseURL**: `https://my.ncu.edu.cn/portal-api/`
- **Header（必须）**:
  ```
  x-id-token: <localStorage token>
  x-device-info: PC
  x-terminal-info: PC
  ```
  实测 `Authorization: Bearer` 无效

### 3.2 端点清单（来自 portalComponent~e8f7a9e7.48c2410f.js）
| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `v1/calendar/share/schedule/getEvents?startDate&endDate&reqType&calendarId` | 周/月视图主查询 |
| GET | `v1/calendar/share/schedule/getSection?startDate` | 节次时间表 |
| GET | `v1/calendar/share/schedule/getWeekOfTeaching?today` | 教学周/学期 |
| GET | `v1/calendar/share/schedule/getSchedule?scheduleId` | 单条详情 |
| GET | `v1/calendar/share/schedule/getCalendar` | 日历列表 |
| GET | `v1/calendar/share/schedule/getPersonlCalendar` | 个人日历 |
| GET | `v1/calendar/share/subscribe/page?pageType=subscribed` | 已订阅 |

### 3.3 getEvents
```
GET /portal-api/v1/calendar/share/schedule/getEvents?startDate=2026-08-31&endDate=2026-09-06&reqType=WeekView
Headers: x-id-token, x-device-info, x-terminal-info
```
响应 `code:0 data.schedule["2026-09-04"].calendarList[]` 含 `title/startTime/endTime/address/calendarName`。未带 token 返回 `{"code":-1,"message":"没有访问权限01"}`

### 3.4 getSection
```
GET /portal-api/v1/calendar/share/schedule/getSection?startDate=2026-09-03
```
```json
{"code":0,"data":{"data":[
  {"section":"一","startTime":"08:00","endTime":"08:40"},
  {"section":"二","startTime":"08:50","endTime":"09:30"},
  {"section":"三","startTime":"09:50","endTime":"10:30"},
  {"section":"四","startTime":"10:40","endTime":"11:20"},
  {"section":"五","startTime":"11:30","endTime":"12:10"},
  {"section":"六","startTime":"14:00","endTime":"14:40"},
  {"section":"七","startTime":"14:50","endTime":"15:30"},
  {"section":"八","startTime":"15:50","endTime":"16:30"},
  {"section":"九","startTime":"16:40","endTime":"17:20"},
  {"section":"十","startTime":"17:30","endTime":"18:10"},
  {"section":"十一","startTime":"19:00","endTime":"19:40"},
  {"section":"十二","startTime":"19:50","endTime":"20:30"},
  {"section":"十三","startTime":"20:40","endTime":"21:20"}
]}}
```

### 3.5 getWeekOfTeaching
```
GET /portal-api/v1/calendar/share/schedule/getWeekOfTeaching?today=2026-08-31,2026-09-07...
-> {"code":0,"data":{"code":0,"data":{"semester":"1","date":[-1]}}}
```
`-1` 表示未开学

---

## 4. 研究生系统真课表（gmsstu.ncu.edu.cn）★

### 4.1 认证
`https://gmsstu.ncu.edu.cn/index` iframe 需 CAS Cookie：
```
Cookie: cn_com_southsoft_gmis_stu=1889975a-...; SESSION=...; TGC=...
```
混淆前缀 `TXlIZWFydFdpbGxHb09u` = Base64("MyHeartWillGoOn")，前 32 位 Hex 固定，**后 80 位 Hex 随登录会话轮换**（2026-09-06 实测：旧路径在新会话一律 `{"code":500,"msg":"服务器异常"}`；跨会话 GET 旧路径 404）。不能长期硬编码，须每次登录后从会话内页面重新发现（`GmsstuClient.discover_kb_url`：拉 `/index` → 逐页找含 `kblx` 的课表页 → 提取其 POST url）。

### 4.2 课表主接口
```
POST https://gmsstu.ncu.edu.cn/TXlIZWFydFdpbGxHb09u01E8D4410B737A813BDBF14D3F0C8535E67EAD08C30ABFDF401A9480D9F0633D7D7F4E773764713FA35A5BB072274B5270ABA6F0FAA9D541
Content-Type: application/x-www-form-urlencoded
X-Requested-With: XMLHttpRequest
Body: kblx=xs&termcode=202620271
```
| 参数 | 说明 |
|---|---|
| kblx | 固定 xs |
| termcode | 202620271 = 2026-2027 第1学期；202620272 = 第2学期 |

响应:
```json
{"rows":[
  {"jcid":"3","mc":"第三节 09:50~10:30","sjbz":"上午","z1":null,"z5":"组合数学[5-15周] 幸玮[前湖北校区研究生院316]","z6":"工程伦理[6-6周]刘韬[基础实验大楼A106]"},
  {"jcid":"6","mc":"第六节 14:00~14:40","z1":"机器学习[1-11周]胡书凡[前湖北校区研究生院316]","z3":"高级计算机系统结构[1-16周]张宇成[前湖北校区研究生院215]"}
]}
```
`jcid 1-13` 对应第一节-第十三节，`z1-z7` 周一到周日，值为 `课程名[周次]教师[教室]` 或 null

**实测样例课表（2026-2027-1）：**
- 周一 6-8节 机器学习 胡书凡
- 周三 3-4节 高级计算机系统结构 张宇成；8-10节 数据科学与工程 王洋洋；11-13节 最优化 肖艳阳
- 周四 4节 自然辩证法 康琳
- 周五 3-5节 组合数学 幸玮
- 周六/日 3-5/6-10节 工程伦理 刘韬

解析正则：`(.+?)\[(.+?)\]\s*(\S+)\[(.+?)\]` → name/weeks/teacher/room

---

## 5. Python SDK 示例

### 融合门户
```python
import requests
BASE="https://my.ncu.edu.cn/portal-api/"
h={"x-id-token":token,"x-device-info":"PC","x-terminal-info":"PC"}
requests.get(BASE+"v1/calendar/share/schedule/getEvents",
  params={"startDate":"2026-08-31","endDate":"2026-09-06","reqType":"WeekView"}, headers=h).json()
```

### 研究生课表
```python
import requests, re
session = requests.Session()  # 已通过 CAS 登录，cookies 含 cn_com_southsoft_gmis_stu
url="https://gmsstu.ncu.edu.cn/TXlIZWFydFdpbGxHb09u01E8D4410B737A813BDBF14D3F0C8535E67EAD08C30ABFDF401A9480D9F0633D7D7F4E773764713FA35A5BB072274B5270ABA6F0FAA9D541"
resp=session.post(url, data={"kblx":"xs","termcode":"202620271"},
  headers={"Content-Type":"application/x-www-form-urlencoded","X-Requested-With":"XMLHttpRequest"})
rows=resp.json()["rows"]
for row in rows:
    for wd,key in enumerate(["z1","z2","z3","z4","z5","z6","z7"],1):
        cell=row[key]
        if cell:
            m=re.match(r"(.+?)\[(.+?)\]\s*(\S+)\[(.+?)\]",cell)
            print(m.groups(), wd, row["jcid"])
```

---

## 6. 开发建议
- 本地 SQLite 按 termcode-weekday-sections 索引，getSection 本地缓存
- 优先用 gmsstu 的 weeks 字符串，portal 的 getWeekOfTeaching 仅校准
- gmsstu 混淆 URL 的 hex 尾随登录会话轮换（见 4.1），SDK 已在静态路径失效时自动按会话重新发现端点；自动发现也失败（如登录页结构大改）才需人工抓包更新 `Settings.gmsstu_obfuscated_path`