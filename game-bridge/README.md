# StardewAgentBridge (P1-04)

SMAPI Mod，StardewAgent 的游戏桥接层。采集游戏状态推送给本机 Python Controller（`agent/`）；玩家手动开启自动控制后，执行 Controller 下发的基础动作（移动、转向、切换快捷栏）。不修改存档，不修改游戏程序集。

## 环境

| 项目 | 版本 |
|------|------|
| Stardew Valley | 1.6.15 build 24356（x86_64，经 Rosetta 2 运行） |
| SMAPI | 4.5.2 |
| .NET SDK（构建用） | 10.0.403 ARM64 |
| 目标框架 | net6.0（游戏自带 .NET 6.0.32 运行时） |
| NuGet | Pathoschild.Stardew.ModBuildConfig 4.4.0 |

游戏路径在 `StardewAgentBridge.csproj` 中写死为 `/Applications/Stardew Valley.app/Contents/MacOS`。路径不同时修改 `<GamePath>`。

## 构建与安装

```bash
cd game-bridge/StardewAgentBridge
dotnet restore
dotnet build
```

ModBuildConfig 在构建后自动把 `StardewAgentBridge.dll` 和 `manifest.json` 复制到：

```
/Applications/Stardew Valley.app/Contents/MacOS/Mods/StardewAgentBridge/
```

构建不会修改游戏原始程序集或其他 Mod。

## 启动

通过 SMAPI 启动游戏（安装 SMAPI 后，从 Steam 或直接运行游戏即走 SMAPI 启动器）。SMAPI 控制台窗口出现即表示正常。

## 日志检查

日志文件：`~/.config/StardewValley/ErrorLogs/SMAPI-latest.txt`

```bash
grep "StardewAgentBridge" ~/.config/StardewValley/ErrorLogs/SMAPI-latest.txt
```

预期输出：

- 游戏启动后：`Mod loaded successfully`
- 进入存档后：`World loaded.`，随后输出日期、季节、时间、地图、tile 坐标、体力
- 每天开始时：`New day: Year X, <season> <day>, time 06:00`

## 状态快照（P1-02）

`GameStateObserver` 只在 SMAPI 主线程读取 Game1 和 Farmer，结果写入不可变的 `GameStateSnapshot`。其他线程通过 `ModEntry.LatestSnapshot`（对象）或 `ModEntry.LatestSnapshotJson`（snake_case JSON）读取。

快照分为独立的 DTO（`State/`），每个由 `Observers/` 下的一个观察器采集：

| 段 | DTO | 观察器 |
|----|-----|--------|
| `world` | WorldState：日期、星期、时间、天气、地图、暂停 | WorldObserver |
| `player` | PlayerState：坐标、朝向、体力、生命、技能 | PlayerObserver |
| `inventory` | InventoryState：容量、已用/空格、所有已解锁格子 | InventoryObserver |
| `toolbar` | ToolbarState：快捷栏 12 格、选中索引、手持物品 | InventoryObserver |
| `equipment` | EquipmentState：帽子、上衣、裤子、鞋、双戒指、饰品 | EquipmentObserver |
| `finance` | FinanceState：金币、累计收入、出货箱估值 | FinanceObserver |
| `quests` | QuestState：普通任务与特殊订单（分开） | QuestObserver |

| 触发 | 刷新的段 |
|------|----------|
| SaveLoaded / DayStarted | 全部 |
| TimeChanged | world、player；调试模式输出 `Time changed: 06:00 -> 06:10` |
| InventoryChanged（本地玩家） | inventory、toolbar、equipment、finance |
| OneSecondUpdateTicked | world、player、toolbar、finance；每 `FullRefreshIntervalSeconds` 秒刷新全部 |
| ReturnedToTitle | 清空，`world_ready=false`，各段为 null |

`revision` 只在内容变化时递增（忽略时间戳）。调试模式下每次变化输出一行摘要，例如 `[rev 42] player.tile_x: 9 -> 10`，不输出完整 JSON。某段读取抛异常时保留上一次的值并记录一次错误。

### 配置

首次启动后 SMAPI 生成 `Mods/StardewAgentBridge/config.json`：

```json
{
  "DebugMode": true, "FullRefreshIntervalSeconds": 5, "MaxChangesPerLog": 12,
  "TransportEnabled": true, "ControllerUrl": "http://127.0.0.1:8765",
  "PushIntervalSeconds": 1, "RequestTimeoutSeconds": 2,
  "ActionsEnabled": true, "ActionPollIntervalMs": 200, "AutomationAllowedSaves": "Testone",
  "ToggleAutomationKey": "F7", "EmergencyStopKey": "F8"
}
```

- `DebugMode`：设为 `false` 后不再输出时间变化和字段变化日志，快照照常更新。
- `FullRefreshIntervalSeconds`：全量刷新间隔，用于发现没有 SMAPI 事件的变化（任务进度、装备、背包升级）。
- `MaxChangesPerLog`：每行日志最多列出的变化字段数，多出部分显示为 `+N more`。

- `TransportEnabled`：是否推送给 Controller。
- `ControllerUrl`：Controller 地址，只接受 http + 回环地址（127.0.0.1、::1、localhost），否则禁用推送并记录错误。
- `PushIntervalSeconds`：推送间隔（现实秒）。
- `RequestTimeoutSeconds`：单次请求超时（连接 + 响应）。

- `ActionsEnabled`：是否轮询 Controller 的动作。开启后自动控制仍是关闭状态。
- `ActionPollIntervalMs`：自动控制开启时的轮询间隔（关闭时 1 秒）。
- `AutomationAllowedSaves`：允许开启自动控制的存档名，逗号分隔，匹配存档文件夹名或其 `<名字>_` 前缀。为空时任何存档都不能开启。
- `ToggleAutomationKey`：开启/关闭自动控制（关闭 = 手动接管）。
- `EmergencyStopKey`：紧急停止。

旧版 config.json 缺少新字段时，SMAPI 使用默认值。

### 控制台命令

- `agent_state`：打印当前内存快照摘要。
- `agent_state json`：打印完整 snake_case JSON。
- `agent_control on|off|stop|status`：开启自动控制 / 手动接管 / 紧急停止 / 查看状态（是否开启、当前动作、阻止原因、Controller 链路）。

## 推送（P1-03）

代码在 `Transport/`，不引用 SMAPI 或游戏类型。

| 类 | 线程 | 职责 |
|----|------|------|
| `ObservationPublisher` | 主线程 | 管理 session_id 与 sequence，把不可变快照序列化为 envelope 字节 |
| `ObservationEnvelope` | 主线程 | 生成 `{schema_version, session_id, sequence, created_at_utc, bridge_version, snapshot}`，snapshot 使用 `StateJson.Options` |
| `ObservationSender` | 后台单任务 | 单槽“最新覆盖”缓冲；HttpClient POST `/v1/observations`；退避重试；日志排队回主线程输出 |

- 主线程每 `PushIntervalSeconds` 秒在 OneSecondUpdateTicked 中刷新快照、序列化、交给发送者，不等待网络。后台只拿到字节数组，不碰 Game1。
- 网络慢或断开时，新 payload 覆盖未发送的旧 payload，不会堆积（`SupersededCount` 统计被覆盖数）。
- 失败（连接拒绝、超时、5xx）后退避 1/2/4/5 秒重试；有更新的 payload 时改发新的。HTTP 409（重复/旧序号）视为已送达；其他 4xx 只警告一次且不重试。
- 日志：断线时一条 Warn，之后最多每 30 秒一条；恢复时一条 Info（`Reconnected ... after N failed attempt(s)`）。
- 会话：加载存档后第一个 world_ready=true 快照生成新 session_id（Guid N 格式），sequence 从 1 递增。返回主菜单时发送一条 world_ready=false 并清空会话；主菜单期间不推送。游戏退出时 `Dispose` 发送 world_ready=false 并最多等待 1 秒。未送达时由 Controller 的超时（默认 5 秒）把状态标为 stale。
- HttpClient 不走系统代理，连接池每分钟回收一次，以便 Controller 重启后重新连接。

离线测试工具 `tools/TransportProbe`（net10.0，直接编译 Mod 的 State/Serialization/Transport 源文件）：

```bash
cd game-bridge/tools/TransportProbe && dotnet build
dotnet bin/Debug/net10.0/TransportProbe.dll fixtures ../../../agent/tests/fixtures   # 生成 schema 测试样例
dotnet bin/Debug/net10.0/TransportProbe.dll send http://127.0.0.1:8765 20            # 模拟 20 秒存档后返回主菜单
```

## 动作执行（P1-04）

代码在 `Actions/` 与 `Transport/ActionChannel.cs`、`Transport/ActionContracts.cs`。

| 类 | 线程 | 职责 |
|----|------|------|
| `ActionChannel` | 后台单任务 | POST `/v1/actions/claim` 轮询（兼心跳）、上报状态、发送停止请求；事件经有界队列交给主线程 |
| `ActionExecutor` | 主线程 | 状态机与安全检查，一次只执行一个动作；不引用 SMAPI，可单元测试 |
| `SmapiGameControl` | 主线程 | 读取位置、判断能否操作、`IInputHelper.Press` 按键 |
| `AutomationController` | 主线程 | 把以上三者接到 UpdateTicking、按键与控制台命令 |

每个 UpdateTicking（游戏读取输入之前）：处理 Controller 事件 → 推进当前动作 → 发布 claim 状态。

移动（`move_direction`）：

- 每 tick 调用一次 `Helper.Input.Press(按键)`，按满 `duration_ticks`（1–60）后停止调用，SMAPI 在下一输入 tick 自动释放。
- 按键取自玩家当前键位设置 `Game1.options.moveUpButton` 等，不假设 WASD；没有键盘绑定时 FAILED `no_keyboard_binding`。
- 每 tick 读取像素位置。按住 4 tick 后，若连续 10 tick 位置不变，判定被阻挡，提前结束为 FAILED `blocked: ...`；按满时长但总位移 < 1 px 也是 FAILED。结果中的 `end_position` 是实际位置，不是目标位置。
- 走进传送点换地图时立即停止按键，记为 SUCCEEDED（`distance_pixels` 为 null）。
- 硬性上限：60 tick、3 秒实际时间（TIMED_OUT）。

`face_direction`（`Farmer.faceDirection`）和 `select_slot`（`Farmer.CurrentToolIndex`）在一个 tick 内完成。

安全控制：

- 每次加载存档时自动控制都是关闭的，必须按 F7 或执行 `agent_control on` 开启。只在单人模式、`AutomationAllowedSaves` 中的存档允许开启。
- F8（`EmergencyStopKey`）：在主线程立即取消当前动作、关闭自动控制，并请求 Controller 清空队列。不依赖 Controller 是否在线。
- 手动接管（再按 F7 或 `agent_control off`）：关闭自动控制，取消当前动作，清空 Controller 队列。
- 以下情况拒绝新动作，执行中的动作也会立即取消：未加载存档、多人/分屏、**游戏窗口不在前台**、暂停、菜单打开、对话、事件/过场、屏幕淡入淡出、控制冻结、正在使用工具。
- 存档会话变化（返回主菜单、重新加载）：取消当前动作，关闭自动控制，清空本会话记住的动作 ID。
- Controller 失联（最近一次请求失败，或超过 2 秒没有成功通信）：取消当前动作。Controller 重启（实例 ID 变化）：丢弃本地动作状态，不重放。
- 同一会话内重复的 action_id 一律 REJECTED。
- `stop` 不排队：Controller 把 `stop_seq` 放在每次轮询响应里，Bridge 看到更大的值就取消当前动作。

**窗口焦点**：反编译确认，游戏窗口失去焦点时 `Game1._update` 不调用 `UpdateControlInput`（默认设置下整个游戏也会暂停），按键不会生效。因此窗口不在前台时 Bridge 报告 `game_window_inactive`，不领取动作；已提交的动作在 `ttl_seconds` 内等待。用 curl 提交动作后，需在 TTL 内切回游戏窗口。

## 测试

```bash
cd game-bridge/tests/StardewAgentBridge.Tests && dotnet test     # ActionExecutor 逻辑 + 动作线路格式
cd game-bridge/tools/TransportProbe && dotnet build
dotnet bin/Debug/net10.0/TransportProbe.dll bridge http://127.0.0.1:8765 30   # 真实传输/执行代码 + 模拟游戏
```

## 常见错误

| 现象 | 原因 / 处理 |
|------|-------------|
| 构建报错找不到游戏路径 | 检查 csproj 中 `<GamePath>` 是否指向包含 `Stardew Valley.dll` 的目录 |
| `dotnet new` 不接受 `-f net6.0` | .NET 10 SDK 模板不含 net6.0，生成默认类库后手动改 csproj |
| SMAPI 提示 mod 缺少 manifest | 确认 `Mods/StardewAgentBridge/manifest.json` 存在且 `EntryDll` 与 DLL 名一致 |
| SMAPI 提示 API 版本过低 | 升级 SMAPI，或核对 manifest 中 `MinimumApiVersion` |
| 复制到 Mods 失败（权限） | 游戏目录需对当前用户可写；不要用 sudo 构建 |
| 构建时游戏正在运行 | DLL 被占用可能复制失败，关闭游戏后重新构建 |
| 主菜单无状态输出 | 正常现象，状态只在存档加载后读取 |
| `Controller unreachable ... Connection refused` | Python Controller 未启动；启动 `uv run stardew-agent` 后自动重连，游戏不受影响 |
| `Controller rejected an observation (HTTP 422)` | C# DTO 与 `agent/src/stardew_agent/schema.py` 不一致；按 agent/README 重新生成 fixtures 并对齐 |
| `Transport disabled: ControllerUrl ...` | ControllerUrl 不是回环 http 地址 |
| `Automation NOT armed: save '...' is not in AutomationAllowedSaves` | 把存档名加入 config.json 的 `AutomationAllowedSaves` |
| 动作一直 QUEUED 然后 EXPIRED | 自动控制没开（F7），或游戏窗口不在前台，或有菜单/对话；`agent_control status` 查看 `block` |
| 动作 REJECTED `automation_disabled` / `session_mismatch` | 未开启自动控制，或提交时用了旧存档的 session_id |
