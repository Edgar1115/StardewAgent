# StardewAgent 开发进度记录

本文档用于记录 StardewAgent 项目的 Agent 开发进度，包括目标、已完成事项、进行中的工作、遇到的问题和后续计划。每次开发结束后更新。

## 项目目标

开发 Stardew Valley Autonomous Agent，让 AI 完全自主经营星露谷农场。架构为 C# SMAPI Mod（游戏桥接）+ Python Agent（决策）。

## 当前状态

- 阶段：P1-04 Action Executor（完成，游戏内验收通过；非 WASD 键位未验证）
- 最后更新：2026-11-05
- 代码位置：`game-bridge/StardewAgentBridge/`（Mod 0.3.0）、`agent/`（Python Controller）
- 构建：`dotnet build` 成功（0 警告 0 错误），已部署到 `Mods/StardewAgentBridge/`；`uv run pytest` 68 项通过；`dotnet test` 30 项通过
- 下一步：补 P1-02 剩余验证与日志降噪

### P1-04 验证状态

| 项目 | 状态 | 依据 |
|------|------|------|
| Python Schema、队列状态机、幂等、租约、stop、会话、重启 | 已验证 | pytest `test_actions.py` 32 项 |
| C# ActionExecutor 逻辑（阻挡、stop、禁用、菜单中断、失联、超时、重复 ID、旧会话、Controller 重启） | 已验证 | `dotnet test` 33 项（含线路格式 3 项、短暂阻塞等待 3 项） |
| 真实 Transport + Executor 对接真实 Controller（模拟游戏） | 已验证 | TransportProbe bridge：撞墙 FAILED blocked、向右 SUCCEEDED、stop 中断 CANCELLED、select_slot、Controller 中途被杀后 26 tick 内取消动作、重启后旧动作 404 不重放、回主菜单后提交被拒 |
| SMAPI 构建、部署 0.3.0 | 已验证 | dotnet build |
| SMAPI 加载 0.3.0、`Actions enabled` 日志 | 已验证 | 2026-11-06 SMAPI 日志 15:30:15 |
| Python 发送 move_direction 后角色实际移动；位置出现在 Snapshot | 已验证 | 2026-11-06 农场空地移动成功；`[rev 19] player.tile_x: 64 -> 62` |
| 实际键位映射（非 WASD）生效 | 未验证 | 代码读取 `Game1.options.move*Button`；未在游戏中改键测试 |
| stop 中断真实移动 | 已验证 | 2026-11-06 17:01 向左移动 25 tick 后 CANCELLED `stopped: kiro stop test` |
| F8 紧急停止 | 已验证 | 17:07:26 移动中按 F8：38 tick 后 CANCELLED `emergency_stop`，之后提交均 `automation_disabled`。F8 在主线程本地处理，不经过 HTTP；Python 关闭状态下按 F8 未单独测试 |
| 菜单打开时不移动 | 已验证 | 17:09:35 打开背包：CANCELLED `interrupted: menu_open: GameMenu`（14 tick）；菜单期间动作未被领取，TTL 后 EXPIRED。17:09:44 窗口失焦时 CANCELLED `interrupted: game_window_inactive` |
| 真实障碍物前判定 blocked | 已验证 | FarmHouse (9,9) 向下：朝向变化、位移 0，10 tick 后 FAILED blocked；农场向左 136 px 后撞到杂物 FAILED blocked |
| 返回主菜单后旧动作失效 | 已验证 | 17:11 标题画面提交 `session_not_live`；重新加载后自动控制为关闭，旧会话提交 `session_mismatch`，新会话提交 `automation_disabled` |
| Python 重启后不重放（游戏内） | 已验证 | 17:05:40 移动中杀掉 Controller：44 tick 时 CANCELLED `controller_unreachable`；重启后旧动作 404，角色未再移动 |
| 游戏窗口失焦时不领取动作 | 已验证 | 切到终端时 `/v1/control/status` 显示 `bridge_block_reason: game_window_inactive`，切回后动作才开始 |
| 同一 action_id 重复提交不重复执行（游戏内） | 已验证 | 第二次提交 test-left-0001 未到达 Bridge |

### P1-03 验证状态

| 项目 | 状态 | 依据 |
|------|------|------|
| Controller 独立启动、GET /health | 已验证 | `uv run stardew-agent`，curl /health 返回 ok |
| Pydantic 校验（C# 生成的样例 + 21 种无效输入） | 已验证 | pytest |
| 乱序、重复、旧会话迟到包、无效 Schema 不覆盖状态 | 已验证 | pytest |
| 超时变 stale、返回主菜单变 not_ready | 已验证 | pytest（假时钟） |
| C# Transport 发送、Controller 关闭时限频警告、重启后重连、结束会话 | 已验证（离线） | TransportProbe 使用真实 Transport 代码对真实 uvicorn 测试 |
| Mod 编译、部署 | 已验证 | dotnet build |
| SMAPI 加载 0.2.0、游戏内推送真实快照 | 已验证 | SMAPI 日志 17:51：`Transport enabled`、`session a37e9f1d... started`、`Connected to controller` |
| 时间/位置/背包/工具/金币变化传到 Python | 已验证 | 用户手动验收（curl /latest） |
| Python 关闭时游戏正常、重启后重连 | 已验证 | 日志 17:53:18 `Controller unreachable`，17:53:30 `Reconnected ... after 4 failed attempt(s)` |
| 返回主菜单后 Python 显示 not_ready | 已验证 | 日志 17:53:50 `transport session ... closed`；用户 curl 确认 |
| 存档中退出游戏后 Python 不显示 live | 已验证 | 用户手动验收 |
| 退出游戏时 Dispose 是否送达 world_ready=false | 未确定 | 最新 SMAPI 日志里是先回主菜单再退出；无论哪条路径，都有 5 秒超时兜底 |
| 窗口失焦时 OneSecondUpdateTicked 是否继续 | 未验证 | 若停止，Controller 会显示 stale |

### P1-02 验证状态

依据：2026-11-05 测试存档 Testone 的 SMAPI 日志（春 1 日 06:00–08:10）。

| 功能 | 状态 | 日志依据 |
|------|------|----------|
| SMAPI 加载、`Mod loaded successfully` | 已验证 | 加载列表含 StardewAgentBridge 0.1.0 |
| 日期、星期、时间、天气 | 已验证 | Year 1 spring 1 Monday 06:00 Sun；时间 600→810 每 10 分钟更新 |
| 位置、地图、朝向 | 已验证 | FarmHouse→Farm→BusStop→Town，坐标与朝向连续变化 |
| 暂停 / 时间流逝 | 已验证 | 打开菜单时 `time_passing` true→false |
| 快捷栏选中物品 | 已验证 | 斧头、锄头、喷壶、十字镐、镰刀切换；镰刀识别为 `(W)47` weapon |
| 拾取物品、背包内移动 | 已验证 | 纤维 `(O)771` 进入第 5 格，后移到第 6、9 格；空格保留索引 |
| 体力 | 已验证 | 使用十字镐 270→268 |
| 返回主菜单清空 | 已验证 | `world_ready: true -> false`，各段为 null |
| 状态不变时 revision 不增加 | 已验证 | 16:25–16:45 无变化日志 |
| 消耗 / 丢弃物品 | 未验证 | |
| 背包升级（容量） | 未验证 | |
| 装备更换 | 未验证 | |
| 买卖、金币、出货箱估值 | 未验证 | 仅确认初始 500g shared |
| 任务内容与进度 | 未验证 | 仅确认初始有 1 个任务 |
| 特殊订单 | 未验证 | |
| 生命值变化 | 未验证 | 仅确认初始 100/100 |
| 跨天 DayStarted | 未验证 | |
| 快捷栏换行（Tab） | 未验证 | |
| 技能等级 | 未验证 | |

### 已知限制

- `shipped_income_today` 固定为 null：游戏夜间结算后不保存已支付金额。替代：睡前读 `shipping_bin_estimated_value`，或比较过夜前后金币。
- 出货箱估值不含迷你出货箱。
- `individual_money_earned` 仅在独立钱包开启时有值。
- 送物任务无部分进度，收获/制作任务无计数。
- 无 SMAPI 事件的变化（装备、任务进度、背包升级）最多延迟 5 秒（`FullRefreshIntervalSeconds`）。

### 已知问题

- 日志过长：初始快照一行列出 300+ 字段；物品增减逐个子字段列出，被截断为 `+18 more`；清空格子时 `item: (none) -> null` 与一串 `removed` 重复。只影响日志可读性，快照数据正确。

## 进度日志

### 2026-11-05
- 创建项目目录与本进度记录文档。
- P1-01：创建 `game-bridge/StardewAgentBridge`（net6.0 类库，ModBuildConfig 4.4.0）。
  - `ModEntry` 订阅 GameLaunched / SaveLoaded / DayStarted，只读输出日期、季节、时间、地图、tile 坐标、体力。
  - `dotnet build` 成功（0 警告 0 错误），自动部署到 `Mods/StardewAgentBridge/`。
- P1-01 游戏内验证通过：Mod 加载成功，SaveLoaded 输出时间 600、FarmHouse (9,9)、体力 270。
- 问题：只输出初始状态，不跟踪变化。修复（P1-02 持续状态跟踪）：
  - 新增 `GameStateSnapshot`（不可变 record）、`GameStateObserver`（主线程采集）、`ModConfig`（DebugMode）。
  - 新增事件 TimeChanged、OneSecondUpdateTicked、ReturnedToTitle；新增控制台命令 `agent_state`。
  - `dotnet build` 成功（0 警告 0 错误），已部署。
- P1-02 扩展完整状态感知：
  - 新增 DTO（`State/`）：World、Player、Inventory、Toolbar、Equipment、Finance、Quest；快照改为 `revision`（仅内容变化时递增）。
  - 新增观察器（`Observers/`），每段独立采集；新增 InventoryChanged 事件，每 5 秒全量校验。
  - System.Text.Json + 自定义 snake_case 策略（.NET 6 无内置 SnakeCaseLower）；`agent_state json` 输出完整 JSON。
  - 所有 API 已在本机 1.6.15 / SMAPI 4.5.2 程序集中反射和反编译确认。
  - `dotnet build` 成功（0 警告 0 错误），已部署；离线测试 22 项通过（snake_case、null 字段、diff）。
- P1-02 游戏内首轮验证：结果见上方“验证状态”表。
- P1-04 Action Executor：
  - Python：`actions.py`（动作 Schema、内存队列、租约、stop、会话校验）；新增 POST /v1/actions、POST /v1/actions/claim、POST /v1/actions/{id}/status、GET /v1/actions/{id}、POST /v1/control/stop、GET /v1/control/status。
  - C#：`Transport/ActionChannel.cs`、`Transport/ActionContracts.cs`、`Transport/LocalHttp.cs`（与 ObservationSender 共用）；`Actions/`（ActionExecutor、IGameControl、SmapiGameControl、AutomationController）。ModConfig 增加 5 项；新增控制台命令 `agent_control`；manifest 0.3.0。
  - 新增 `game-bridge/tests/StardewAgentBridge.Tests`（xunit）；TransportProbe 增加 `bridge` 模式。
  - 反编译确认：`IInputHelper.Press` 按键在下一输入 tick 释放；窗口失焦时 `UpdateControlInput` 不执行。
  - 离线测试发现并修复：Controller 断开后执行器要等 2 秒超时才停；改为最近一次请求失败即视为失联。
  - 修改日志：Controller 重启时只在确实丢弃了动作时才写 “dropped action ... without replaying it”（已构建，下次启动游戏生效）。
  - 游戏内首次测试（2026-11-06）：动作被 REJECTED `using_tool`。原因：用鼠标点击游戏窗口切回焦点时，这次点击同时挥动了手中的工具，Bridge 恰好在挥动期间领到动作并立即拒绝。修复：`using_tool`、`player_cannot_move`、`screen_fading`、`controls_frozen`、`game_window_inactive` 视为短暂阻塞，领到的动作最多等待 2 秒再开始，超时才 REJECTED。另外切回游戏应使用 Cmd+Tab，点击游戏画面会触发工具（如锄地、砍树）。
- P1-03 本地 HTTP 通信：
  - 新增 `agent/`（uv，Python 3.13，FastAPI 0.162.0 / Uvicorn 0.54.0 / Pydantic 2.14.2）：GET /health、POST /v1/observations、GET /v1/observations/latest；内存存储；会话/序号校验；新鲜度。
  - 新增 `Transport/`（ObservationPublisher、ObservationEnvelope、ObservationSender）；ModConfig 增加 4 项；ModEntry 在 SaveLoaded、每秒、ReturnedToTitle、Dispose 时推送。manifest 升到 0.2.0。
  - 新增 `game-bridge/tools/TransportProbe`：生成 schema 样例、模拟推送。
  - 游戏内验收通过（Testone 存档）：推送、状态变化、Python 停止与重连、回主菜单 not_ready、退出后非 live。
  - 离线端到端：Controller 未启动时警告 → 启动后连上 → 杀掉后警告 → 重启后 `Reconnected after 3 failed attempt(s)` → 结束会话后 /latest 为 not_ready。

## 待办事项

- [x] P1-01 游戏内验证：进入测试存档，确认 SMAPI 日志输出真实玩家状态
- [x] P1-02 基础验证：时间、坐标、体力变化同步到快照；返回主菜单后快照清空
- [x] P1-02 扩展部分验证：见“验证状态”表中“已验证”项
- [ ] P1-02 扩展剩余验证：见“验证状态”表中“未验证”项
- [ ] 日志降噪：初始快照只输出摘要，物品增减按格子汇总（如 `inventory.slots[5]: (O)771 x1 -> empty`）
- [x] 设计 C# Mod 与 Python Agent 之间的通信方式（P1-03，HTTP 推送）
- [x] P1-03 游戏内验收（2026-11-05 用户手动验收 6 步通过）
- [x] P1-04 游戏内验收（2026-11-06，非 WASD 键位除外）

## 问题与决策记录

| 日期 | 问题 / 决策 | 结论 | 原因 |
|------|-------------|------|------|
| 2026-11-05 | .NET 10 SDK 的 `dotnet new` 不支持 `-f net6.0` | 生成默认类库后手动改 csproj 为 net6.0 | 游戏运行时为 .NET 6 |
| 2026-11-05 | 游戏路径自动检测 | csproj 显式设置 `GamePath` | macOS .app 目录结构，显式设置更稳定 |
| 2026-11-05 | 读取状态的时机 | Entry() 只注册事件，SaveLoaded 中先检查 `Context.IsWorldReady` 和 `Game1.player` | 避免在主菜单访问未初始化的玩家对象 |
| 2026-11-05 | 快照的线程安全 | 主线程生成不可变 record，用 volatile 引用整体替换 | 后续 HTTP 线程只读引用，不直接碰 Game1，无需加锁 |
| 2026-11-05 | 当日出货收入 | `shipped_income_today` 固定为 null，另给出货箱估值 | 游戏在夜间结算，不保存已支付金额 |
| 2026-11-05 | 任务描述/目标读取 | 读缓存字段 `_questDescription`、`_currentObjective`，不调 getter | getter 会调用 reload 方法写 net 字段，违反只读 |
| 2026-11-05 | 天气读取 | 直接查 `netWorldState.LocationWeather` | `GetWeather()` 对缺失的 context 会生成新天气并消耗随机数 |
| 2026-11-05 | 快捷栏读取 | 每次读 `Items[0..11]`，不缓存格子位置 | 切换快捷栏行会原地旋转 `Farmer.Items` |
| 2026-11-05 | 通信方向 | C# 主动 POST 到 Python，Python 不回调游戏 | Mod 不需要开端口；Python 重启不影响游戏 |
| 2026-11-05 | 发送缓冲 | 单发送者 + 单槽最新覆盖 | 状态只关心最新值；断网时不堆积 |
| 2026-11-05 | 序列化位置 | 主线程生成 envelope 字节，后台只发送 | 后台线程完全不接触游戏对象 |
| 2026-11-05 | 会话与排序 | 每次加载存档新 session_id，序号会话内递增；Python 退役旧 session_id | 防止旧会话迟到包覆盖新状态 |
| 2026-11-05 | 新鲜度 | Python 用接收时的单调时钟判断，默认 5 秒无数据为 stale | 不信任 payload 时间；游戏崩溃时无法发送结束消息 |
| 2026-11-05 | Pydantic 严格度 | extra=forbid、strict、全部字段必填 | C# 改字段而 Python 未改时立即 422，而不是静默丢字段 |
| 2026-11-05 | 动作下发方式 | Bridge 轮询 claim（200 ms），不开端口 | 沿用 P1-03“只由 C# 发起请求”；轮询同时作心跳 |
| 2026-11-05 | stop 的实现 | 计数器 `stop_seq` 随每次轮询返回，不进队列 | 天然优先；响应丢失也会在下一次轮询补上 |
| 2026-11-05 | 输入方式 | 每 tick `IInputHelper.Press`，读 `Game1.options` 键位 | 游戏自己处理碰撞和动画；不写坐标，不用系统级键盘模拟 |
| 2026-11-05 | 阻挡判定 | 按住时连续 10 tick 无位移即 FAILED blocked | 按满时长不等于移动成功 |
| 2026-11-05 | 窗口失焦 | 拒绝并取消动作（`game_window_inactive`） | 失焦时游戏不处理输入，按键无效 |
| 2026-11-05 | 首版动作范围 | move_direction、face_direction、select_slot、stop；不做 use_tool、interact | 后两者会改动世界（锄地、上床睡觉触发存档） |
