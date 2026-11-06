# StardewAgent Controller (P1-04)

本地 HTTP 服务。接收 StardewAgentBridge 推送的游戏状态，校验后在内存保存最新快照；维护动作队列，由 Bridge 领取并在游戏主线程执行。不接 LLM，不做规划。

## 运行

```bash
cd agent
uv sync
uv run stardew-agent                 # 监听 127.0.0.1:8765
uv run stardew-agent --stale-after 5 --port 8765 --log-level info
uv run pytest -q                     # 测试
```

`--host` 只接受回环地址（127.0.0.1、::1、localhost）。接口没有鉴权，不能对外监听。也可用环境变量 `STARDEW_AGENT_HOST`、`STARDEW_AGENT_PORT`、`STARDEW_AGENT_STALE_AFTER`。

## 接口

### GET /health

```json
{"status": "ok", "service": "stardew-agent-controller", "version": "0.1.0", "schema_version": 1,
 "observation": {"status": "live", "is_live": true, "age_seconds": 0.4, "stale_after_seconds": 5.0,
                 "received_at": "2026-11-05T09:36:37+00:00", "session_id": "b1b5...", "sequence": 18}}
```

### POST /v1/observations

请求体（由 C# `ObservationEnvelope.Build` 生成）：

```json
{"schema_version": 1, "session_id": "<32 位 hex>", "sequence": 18,
 "created_at_utc": "2026-11-05T09:36:37.0012345Z", "bridge_version": "0.2.0",
 "snapshot": { ...与 `agent_state json` 相同的快照... }}
```

| 状态码 | 含义 | 返回 |
|--------|------|------|
| 200 | 已接受 | `{"accepted": true, "session_id", "sequence", "new_session"}` |
| 409 | 未覆盖：`duplicate_sequence`、`out_of_order_sequence`、`retired_session` | `{"accepted": false, "reason", "latest_sequence", ...}` |
| 413 | 请求体超过 1 MB | `{"accepted": false, "reason": "body_too_large"}` |
| 422 | JSON 或 Schema 无效 | `{"accepted": false, "reason": "invalid_schema", "errors": [...]}` |

校验规则（`src/stardew_agent/schema.py`）：

- 字段与 C# DTO 一一对应，全部必填（包括值为 null 的字段），出现未知字段即拒绝，类型严格（`"5"` 不会被当成 5）。
- `schema_version` 必须为 1；`sequence` ≥ 1；`session_id` 为 8–64 位 `[A-Za-z0-9_-]`。
- 内容检查：`world_ready=true` 时 7 段都不为 null，`false` 时都为 null；季节、日期、HHMM 时间及 `time_text`；朝向一致；背包格数、已用/空格数、格子顺序、`is_empty` 与 `item` 一致；品质只能是 0/1/2/4；`wallet_scope` 与 `separate_wallets` 一致。

排序与会话（`src/stardew_agent/store.py`）：

- 同一 session：`sequence` 必须递增，重复或更旧的拒绝。
- 新 session_id：替换当前状态（重新加载存档或游戏重启），旧 session_id 进入退役列表，之后的迟到包一律拒绝。
- 新 session 不要求从 1 开始，这样 Controller 中途重启后也能接上。

### GET /v1/observations/latest

```json
{"freshness": {"status": "live", "is_live": true, "age_seconds": 0.09, ...},
 "bridge_version": "0.2.0", "created_at_utc": "...", "snapshot": {...}}
```

`freshness.status`：

| 值 | 含义 |
|----|------|
| `no_data` | 启动后还没收到数据，`snapshot` 为 null |
| `live` | 存档已加载，最后一次接收距今 ≤ `stale_after_seconds` |
| `stale` | 存档曾加载，但已超时没有新数据（游戏退出、崩溃、断网）。仍返回最后的 snapshot 供排查，**不能当作实时状态** |
| `not_ready` | Bridge 报告 `world_ready=false`（返回主菜单）。snapshot 各段为 null，旧存档状态不再暴露 |

新鲜度按 Controller 收到数据的单调时钟计算，不信任 payload 中的时间戳。使用方只应在 `is_live` 为 true 时把 snapshot 当作当前状态。

## 动作 API（P1-04）

实现在 `src/stardew_agent/actions.py`，全部在内存中。Controller 重启后所有动作记录消失，Bridge 也会丢弃从旧实例领取的动作，不会重放。

### POST /v1/actions

```json
{"session_id": "<当前 observation 的 session_id>", "type": "move_direction",
 "params": {"direction": "up", "duration_ticks": 30},
 "action_id": "可选，客户端指定用于幂等重试", "ttl_seconds": 10}
```

| type | params |
|------|--------|
| `move_direction` | `direction`: up/down/left/right；`duration_ticks`: 1–60（60 tick ≈ 1 秒） |
| `face_direction` | `direction` |
| `select_slot` | `slot`: 0–11（快捷栏） |
| `stop` | `reason`（可选）。效果等同 `/v1/control/stop`，Bridge 确认后变为 SUCCEEDED |

| 状态码 | 含义 |
|--------|------|
| 201 | 已排队，返回 `action`（含 `action_id`） |
| 200 | 同一 `action_id` 且内容相同的重复提交，返回已有记录，不会再次执行 |
| 409 | `action_id_conflict`（同 ID 不同内容）、`session_not_live`、`session_mismatch`、`bridge_not_polling`、`bridge_session_mismatch`、`automation_disabled` |
| 422 | Schema 无效（类型严格、禁止未知字段） |
| 429 | `queue_full`（默认最多 16 条排队） |

`ttl_seconds`（1–60，默认 10）内未被领取则变为 EXPIRED。

### GET /v1/actions/{action_id}

返回 `state`、`terminal`、`failure_reason`、各时间点、`history`（每次状态变化），以及 Bridge 上报的 `result`：

```json
{"action_id": "...", "session_id": "...", "type": "move_direction",
 "started_at_utc": "...", "finished_at_utc": "...", "ticks_executed": 49,
 "start_position": {"location_name": "Farm", "tile_x": 64, "tile_y": 15, "x": 4096.0, "y": 960.0, "facing_direction": 0},
 "end_position": {...}, "distance_pixels": 192.0, "blocked": true,
 "failure_reason": "blocked: no movement for 10 ticks"}
```

### POST /v1/control/stop

`{"reason": "...", "source": "..."}`，请求体可为空。立即取消所有排队动作，`stop_seq` 加 1；Bridge 在下一次轮询（约 200 ms）看到后取消正在执行的动作并上报 CANCELLED。返回 `stop_seq`、`cancelled_action_ids`、`active_action_id`。

### GET /v1/control/status

Bridge 链路状态：`bridge_polling`、`automation_enabled`、`bridge_ready`、`bridge_block_reason`、`active_action_id`、`queued`、`stop_seq`、`controller_instance_id`。`/health` 的 `control` 字段相同。

### Bridge 专用

- `POST /v1/actions/claim`：Bridge 轮询，同时也是心跳。请求含 `session_id`、`automation_enabled`、`ready`、`stop_seq_seen`、`controller_instance_id` 等；响应含 `controller_instance_id`、`stop_seq`、`stop_reason`、`action`（带 `lease_id`）或 null。
- `POST /v1/actions/{action_id}/status`：`{"session_id", "lease_id", "state", "failure_reason", "result"}`。404 `unknown_action`，409 `lease_mismatch` / `session_mismatch` / `already_terminal` / `invalid_transition`，422 `result_mismatch` 或 Schema 无效。

### 状态机

```
QUEUED ──claim──▶ CLAIMED ──RUNNING──▶ RUNNING ──▶ SUCCEEDED | FAILED | CANCELLED | TIMED_OUT
  │                 ├─▶ SUCCEEDED | FAILED | CANCELLED | REJECTED | TIMED_OUT   （Bridge 上报）
  │                 ├─▶ TIMED_OUT   租约到期（默认 5 秒没有任何上报）      ◀─ RUNNING 同样适用
  │                 └─▶ CANCELLED   Bridge 会话变化                         ◀─ RUNNING 同样适用
  ├─▶ CANCELLED  stop、会话变化、自动控制关闭
  └─▶ EXPIRED    超过 ttl_seconds 未被领取
stop 动作：QUEUED ──Bridge 确认 stop_seq──▶ SUCCEEDED，或 EXPIRED
```

一致性规则：

- 同一时间最多一个 CLAIMED/RUNNING 动作；Bridge 必须报告 `ready=true` 才会领到下一条。
- 只把动作交给 `session_id` 相同、`automation_enabled=true` 的 Bridge；Bridge 会话变化或关闭自动控制时，排队动作被取消。
- 状态上报必须带领取时的 `lease_id` 和相同的 `session_id`；终态不可更改，重复上报同一终态返回 200 `duplicate`。
- RUNNING 上报会续租；租约到期即 TIMED_OUT，之后的迟到上报返回 409。
- `stop_seq` 绑定 `controller_instance_id`：Controller 重启后计数从 0 开始，Bridge 携带的旧实例计数会被忽略。
- 终态记录最多保留 1000 条，超出时删除最旧的终态记录。

## Schema 同步

`tests/fixtures/*.json` 由真实的 C# 序列化代码生成，测试用它们校验 Pydantic 模型。修改 C# DTO 后重新生成并跑测试：

```bash
cd game-bridge/tools/TransportProbe
dotnet build
dotnet bin/Debug/net10.0/TransportProbe.dll fixtures ../../../agent/tests/fixtures
cd ../../../agent && uv run pytest -q
```

不进游戏的端到端测试：`TransportProbe bridge` 用 Mod 真实的推送、动作通道和执行器代码连接本服务，以 60 Hz 模拟游戏（起点 tile (64,15)，tile 第 12 行有墙）：

```bash
dotnet game-bridge/tools/TransportProbe/bin/Debug/net10.0/TransportProbe.dll bridge http://127.0.0.1:8765 30
```
