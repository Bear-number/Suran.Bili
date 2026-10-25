# Suran.Bili

B 站全功能插件：**视频查询 + 账号能力 + 直播查询**合在一个模块里。免登录能查视频详情、搜索、热门、相关推荐、UP 主与粉丝数、评论区与弹幕；扫码登录后能看收藏夹、稍后再看、观看历史、关注列表；写开关打开后还能点赞、投币、收藏、关注、回评论、发弹幕、投稿入口。直播侧是分区树、各大区在线人气榜、房间/主播在不在播。

## 版本
5.0.0（对应 Alife 客户端 5.0.x；本机 5.0.2 在役客户端）

- 由 `Suran.BiliLive`（5.0.0，四个直播工具）与新的视频/登录/写能力**合并成一个插件**（用户「跟B站直播查询插件合成一个插件行吗」→「Suran.Bili（新 id）」）。直播段的解析逻辑原样搬过来，只把它的 `static HttpClient` 换成共用底座，顺带白拿 buvid 引导、最小间隔与错误码话术。
- 旧的 `Suran.BiliLive` 按用户要求**删掉仓库**（见"收尾"一节的结果）。

## 先说风险
1. **写操作会真的改动账号**：点赞/投币/收藏/关注/评论/弹幕都以你的身份发出，B站与所有平台一样禁止自动化，频繁或机械节奏会被风控（回 `code=137`、`12008`，或干脆 -352）。所以默认 `EnableWrite=false`，且 `RequireConfirm=true`（先把「对哪条、发什么」报给主人，凭确认码才发）。
2. **凭据只落本机**：Cookie 存在 `Storage\BiliQuery\cookies.json`，插件**没有导出 Cookie 的动作**，也不写口令；扫码链接只发给主人本人（拿到那条链接等于能登录他账号）。
3. 登录态会静默失效：`BiliEnv` 会报 `isLogin`，别只看接口 code。

## 依赖
- `Alife.Function.FunctionCaller`
- 无外部程序、无 NuGet/pip 依赖；纯 HTTP。**代理要另说**：B站 web 接口国内直连可用（本机实测），所以默认不配代理。

## AI 函数（27 个）
### 视频侧（免登录）
| 函数 | 说明 |
|---|---|
| BiliVideo(target) | 详情：标题、UP主、分区、时长、发布时间、播放/点赞/投币/收藏/弹幕/评论数、分P与每话 cid。target 吃 BV号 / av号 / 纯 aid / 整条链接 |
| BiliSearch(keyword, order="", page=1, take=8) | 搜视频。`order` 可填 totalrank/click/stow/coins/dm/newest；标题里的 `<em>` 高亮标签会被剥掉再报 |
| BiliHot(page=1, take=10) | 热门列表，带 `no_more` |
| BiliRelated(target, take=10) | B站自己给的相关推荐 |
| BiliUser(mid) | 用户卡片 + 粉丝数（昵称/等级/签名/认证/投稿数）。**按名字找 UID 免登录不行**（`search_type=bili_user` 回 HTML） |
| BiliReplies(target, sort="hot", page=1, take=10) | 评论区。**免登录时 `sort=0`（热度）整个回空列表**，所以热度取不到会自动改按时间取并在回报里说明 |
| BiliSubReplies(target, root, take=10) | 某条主评论下的楼中楼（root 用 BiliReplies 回报里的 rpid） |
| BiliDanmaku(target, page=1, at=0, word="", take=10) | 弹幕：默认给条数/密度/高频词/最密时段；`at=秒` 取那段原文，`word=x` 查提到某词的。分P按 `page` 选，弹幕是按 cid 分片的 |

### 直播侧（免登录，原 Suran.BiliLive）
| 函数 | 说明 |
|---|---|
| BiliLiveAreas(keyword?) | 全部大区或按名字搜分区，直接给出可用于榜单的 parentArea/area |
| BiliLiveRank(parentArea?, area?, count=10, sort?) | 榜单。ID 或中文名都认；人气榜**拿回本地按 online 重排**（接口自带排序到尾部会抖）；误把子分区ID填到大区参数上会被认出来并按子分区查 |
| BiliLiveRoom(source, asUid=false) | 单个房间/主播状态：房间号（长短号都认）、链接、UID 或昵称都能给 |
| BiliLiveOverview(count=5) | 各大区前N，看哪个区人多 |

### 登录与账号（要扫码）
| 函数 | 说明 |
|---|---|
| BiliLogin(action, qrcodeKey) | generate 拿登录链接 / poll 收 cookie（十分钟内有效，成功即存本机并用 nav 复验）/ check 复查 / logout 清除 |
| BiliMe() | 登录账号的昵称、UID、等级、硬币、大会员 |
| BiliFavorites(mid?, take) | 某 UID 的收藏夹列表（不传 mid 就是自己的）；私密的接口不列 |
| BiliFavItems(mediaId, page, take) | 收藏夹里的条目（私密的要登录） |
| BiliWatchLater(page, take) | 我的稍后再看 |
| BiliHistory(size=20, keyword="") | 观看历史，可按标题找看过什么 |
| BiliFollowing(mid?, page, take) | 关注列表（对方设了私密就取不到） |

### 写操作（默认关 + 默认要确认）
| 函数 | 说明 |
|---|---|
| BiliLike(target, undo=false) | 点赞/取消，**发完回查统计**并说明这些数有分钟级延迟 |
| BiliCoin(target, amount=1, alsoLike=false) | 投 1~2 币 |
| BiliFavorite(target, folderId="", undo=false) | 收藏/取消（folderId 从 BiliFavorites 拿） |
| BiliFollow(mid, undo=false) | 关注/取关，回查对方粉丝数 |
| BiliReply(target, text, root=0) | 发评论（root 非 0 就是回复某条） |
| BiliSendDanmaku(target, text, progress=0, cid=0) | 发弹幕（cid 从 BiliVideo 的分P 行拿） |
| BiliConfirm(code) | 用确认码执行登记过的写动作 |

### 诊断
| 函数 | 说明 |
|---|---|
| BiliEnv() | 免登录五条接口通不通、写开关与今日额度、cookie 状态（只报字段名）、最近一次被挡的原因 |
| BiliProbeSite 之类没有：结构变了直接用 BiliEnv 看 code | — |

## 配置
| 键 | 默认 | 说明 |
|---|---|---|
| EnableWrite | false | 写操作总开关，关着时所有点赞/投币/收藏/关注/评论/弹幕都拒绝 |
| RequireConfirm | true | 写动作先登记、要 `BiliConfirm` 才真发 |
| DailyWriteLimit | 20 | 每种写动作每天上限（按动作分别计） |
| MinWriteGapSeconds | 60 | 两次写动作最小间隔 |
| MinRequestGapMs | 350 | **接口层最小间隔**：不带 buvid 时连打第三次起会被粘滞性 412 |
| ProxyUrl | 空 | 直连可用，一般不用填 |
| RequestTimeoutSeconds | 20 | 单次请求超时 |
| MaxCount | 30 | 直播榜单单次上限，钳 1~50（`page_size` 填 100 会被打回 20） |
| DefaultItemCount | 10 | 列表类默认条数 |
| DanmakuScanLimit | 3000 | 弹幕统计取样 |
| CookieFolder | BiliQuery | Cookie 目录（相对 Storage） |
| SearchOrder | 空 | 搜索默认排序，留空=综合 |

## 接口与实测
### 视频侧（2026-10-24 本机直连实测）
- **免登录必须带浏览器 UA + 对应站区 Referer**：`api.bilibili.com/x/web-interface/*` 用 `https://www.bilibili.com/`，搜索用 `https://search.bilibili.com/`，`x/space/*` 用 `https://space.bilibili.com/`
- **`buvid3/buvid4` 是硬需求**：不带指纹连打第三次起 **412 且粘滞**（要等好几分钟），从 `x/frontend/finger/spi` 领一对 buvid 后连打五次全 200 —— 所以底座启动就领一次并常驻，同时保留 `MinRequestGapMs` 做第二道保险
- **`Accept` 头会反杀**：发 `application/json, text/plain, */*` 或 `*/*` 直接 412；不发或发浏览器那串 text/html 才 200。底座干脆不发 Accept
- 通：`web-interface/view?bvid=`、`web-interface/popular`、`web-interface/archive/related`、`web-interface/card?mid=`、`relation/stat?vmid=`（实测 1432707 粉）、`web-interface/search/type?search_type=video`（`numResults`/`seid` 齐全）、`v2/reply?type=1&oid={aid}&sort=2`
- **评论 `sort=0`（热度）免登录整个回空**：同一视频 `sort=2`（时间）回 3 层、`page.count=48602`；`sort=0` 回 `replies:null`、`page.count=0`。所以 BiliReplies 是"热度取空就自动改时间"
- **楼中楼走 `x/v2/reply/reply?root={rpid}`**；`reply/detail` 回 -400
- **弹幕**：`comment.bilibili.com/{cid}.xml`，响应头 `Content-Encoding: deflate`（实测 1200 条）；`x/v1/dm/list_so` 回 HTML，不能用。要按话取，cid 从 `view` 的 `pages[]` 拿
- **UP 主投稿列表拿不到**：`x/space/wbi/arc/search` 就算把 WBI 签名（`nav` 的 img_key+sub_key 混表算 `w_rid`+`wts`）算对、带上 buvid，仍回 **-352 风控校验失败 / -403 / 412**（同一接口不同时刻换着花样挡）。**纯 HTTP 这条路是死的**，插件不假装能做
- **排行榜 `ranking/v2` 回 -352**；**按名字搜 UID 不行**（`search_type=bili_user` 回 HTML）；`search_type=live_user` 反而通（code 0）
- 需要登录的：`history/cursor`、`relation/followings`、`collection/watching/later` 回 -101 或直接甩 HTML；`nav` 未登录回 -101 + `isLogin:false`
- 错误码在 `BiliApi.Hints` 里带"怎么办"：-352 指纹、-101/-104 扫码、12002 评论区关闭或用错 oid、12006 无此楼、12008 内容被屏蔽、137 当日操作上限、-509/-799 频率

### 直播侧（2026-10-15 建立，2026-10-22 复跑，2026-10-24 合并后复测）
- `GET api.live.bilibili.com/room/v1/Area/getList?parent_id=0` → `data` 是**大区数组**，元素 `{id, name, list:[子分区]}`；大区 id 是数字、子分区 id 是字符串，两种取值形态都要兼容（实测 12 大区 / 454 子分区）
- `room/v1/Area/getRoomList?parent_area_id&area_id&sort_type=online&page&page_size` → `data` **直接是数组**（不是 `data.list`），条目里**没有 `live_time`**
- `sort_type=online` **不是严格降序**：一次 50 条实测 33 个逆序对，第 25 条之后开始抖 → 插件本地按 `online` 重排后才叫"人气前N"
- `page_size` 填 100 会被打回 20 条，50 才是有效上限
- `sort_type=live_time`（最新开播）回的房间 `online` 多为 0，且条目没 `live_time`，所以那一档不重排、只按接口顺序列
- `parent_area_id=0&area_id=0` = 全站热门，不需要特殊分支
- `room/v1/Room/get_info?room_id=` 免登录可用，给 `live_status`(0未开播/1直播中/2轮播回放) `title/online/attention/uid/area_name/parent_area_name/live_time/short_id`；短号 `6` 实测解析到 `7734200`；房间不存在回 `code=1 未找到该房间`；**它不给主播昵称**
- `live_user/v1/Master/info?uid=` 给 `info.uname/room_id/follower_num`；实测 `attention` 与 `follower_num` 同值 → UID→room_id→get_info 是一条通链
- 按主播名找房间走 `room/v3/area/getRoomList?...&keyWord=`：`data` 是**对象**、房间在 `data.list`（与 v1 不同）；`data.count` 是分区热度计数**不是命中数**（换词实测都是 11804）→ 搜一页 50 条自己按昵称筛，筛不到就直说、命中多条把候选报回
- **已下架**：`room/v1/Room/getStatusInfoByRoom`、`getStatusInfoByid` 都回 `code=1000003`，别再照旧文档调
- 分区树缓存 30 分钟；数字当 ID、文字先精确后包含、跨区命中多个就报错列候选、子分区ID填错位置会自动纠正并说明

## 扫码登录的实测形状
- `passport.bilibili.com/x/passport-login/web/qrcode/generate` → `data.url`（**字段名是 `url`，没有 `qrcode_url`**）+ `data.qrcode_key`
- `.../qrcode/poll?qrcode_key=` → 外层 `code` 恒 0，状态在 **`data.code`**：0 成功 / 86101 未扫码 / 86090 已扫码待确认 / 86038 已失效；约 180 秒过期
- 成功时 cookie 有**两种下发形态**：`Set-Cookie` 响应头 + `data.url` 的查询串，两种都收；只收一种会出现"说登录成功但 cookie 是空的"
- 存的就是 `SESSDATA / bili_jct / DedeUserID / DedeUserID__ckMd5 / sid`；`bili_jct` 是写接口的 csrf；存完立刻用 `nav` 的 `isLogin/uname/mid` 复验

## 未验证 / 不支持
- **写接口一个都没实机跑过**（要真登录 + 真发东西）。点赞/投币/收藏/关注/回评论的 URL 与参数按公开 web 端点的通用写法 + `csrf` 组装，`BiliSendDanmaku` 的 `x/v2/dm/web/send` 尤其没把握；第一次用请只开一个动作、先人工盯一次
- 弹幕/评论发出后 B站可能吞进屏蔽队列（`12008`），插件只能报"接口收了"，回查才算数
- 投稿只做到"打开投稿页 + 能填标题就填"；选文件是系统对话框，AI 做不了
- 不承诺"人气/播放"是实时快照：都是查那一刻的值

## 验证口径
- 编译门 `.buildcheck/Suran.BiliHostCheck.csproj`（引在役客户端实现程序集，等价热编译）**0 错误 0 C# 警告**
- 反射探针 `scratch/BiliProbe` **275 项 0 失败**（`bash scratch/run-probe.sh BiliProbe`），其中 107 项是从 Suran.BiliLive 搬来的直播段断言，一条没丢。覆盖：目标解析（BV/av/链接/纯数字/短链）、Referer 分站区、查询串转义、错误码话术表、人气与时长文案、弹幕 xml 解析（实体解码/模式标记/时间窗/高频词/垃圾输入）、Cookie 双路收取与"指纹类键不收"、存盘重载、扫码四状态、写闸门（开关/额度/冷却/跨日）、确认码（格式/过期/上限/错码）、JSON 三种取值形态、以及一批源码级检查（默认值保守、没有导出 cookie 的动作、未验证处照实写）
- **探针当场逮到三个真 bug**：① `JsonValue.Create(new JsonObject{…})` 运行期抛 `A JsonNode cannot be used as a value`；② `GetValue<double>` 撞字符串数字抛的是 `InvalidOperationException`，只捕 `FormatException` 会在读评论区时炸；③ 每次调用都 `new BiliApi` → buvid 反复重领、最小间隔形同没有，正是连打几次就 412 的原因（改成共享实例）
- 实机跑过：真接口取到过视频详情/热门/搜索/用户/粉丝/评论/弹幕（1200 条），并据此确认 -352/-403/412 三种挡法
- **没跑过**：任何写动作、扫码登录全流程（要真人扫）、需要登录的四条读路径（形状按文档 + 未登录错误码推断）

## 收尾
旧 `Suran.BiliLive` 的 GitHub 仓库删除结果与本插件的发布状态，见会话记录：`repo` 权限的 classic token 删不掉仓库（`DELETE /repos` 回 403 `Must have admin rights`），那种情况只能主人在网页 Settings → Danger Zone 自己删。
