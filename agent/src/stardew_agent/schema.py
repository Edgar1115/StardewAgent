"""Pydantic mirror of the C# bridge DTOs (game-bridge/StardewAgentBridge/State, GameStateSnapshot.cs).

Field names are the C# property names after StateJson's snake_case policy. The bridge serializes every
property, nulls included, so every field here is required (no defaults) and unknown fields are rejected:
a C# DTO change without a matching change here fails validation instead of silently dropping data.
"""

from datetime import datetime
from typing import Literal, Self

from pydantic import BaseModel, ConfigDict, Field, model_validator

SCHEMA_VERSION = 1

SEASONS = {"spring", "summer", "fall", "winter"}
FACING_NAMES = ("up", "right", "down", "left")
QUALITIES = {0, 1, 2, 4}


class StrictModel(BaseModel):
    # strict: no "5" -> 5 coercion; frozen: stored snapshots can't be mutated by readers.
    model_config = ConfigDict(extra="forbid", strict=True, frozen=True)


# ---- items / inventory -------------------------------------------------------------------------


class ItemState(StrictModel):
    slot_index: int | None
    qualified_item_id: str
    item_id: str
    name: str
    display_name: str
    stack: int = Field(ge=0)
    quality: int
    item_type: str
    type_definition_id: str
    category: int
    upgrade_level: int | None
    combined_rings: list["ItemState"] | None

    @model_validator(mode="after")
    def _check(self) -> Self:
        if self.quality not in QUALITIES:
            raise ValueError(f"quality must be one of {sorted(QUALITIES)}, got {self.quality}")
        return self


class InventorySlotState(StrictModel):
    slot_index: int = Field(ge=0)
    is_empty: bool
    item: ItemState | None

    @model_validator(mode="after")
    def _check(self) -> Self:
        if self.is_empty != (self.item is None):
            raise ValueError(f"slot {self.slot_index}: is_empty={self.is_empty} contradicts item")
        return self


def _check_slot_order(slots: list[InventorySlotState], where: str) -> None:
    for i, slot in enumerate(slots):
        if slot.slot_index != i:
            raise ValueError(f"{where}.slots[{i}] has slot_index {slot.slot_index}")


class InventoryState(StrictModel):
    capacity: int = Field(ge=0)
    used_slots: int = Field(ge=0)
    empty_slots: int = Field(ge=0)
    slots: list[InventorySlotState]

    @model_validator(mode="after")
    def _check(self) -> Self:
        if len(self.slots) != self.capacity:
            raise ValueError(f"inventory has {len(self.slots)} slots, capacity {self.capacity}")
        if self.used_slots + self.empty_slots != self.capacity:
            raise ValueError("inventory used_slots + empty_slots != capacity")
        if sum(not s.is_empty for s in self.slots) != self.used_slots:
            raise ValueError("inventory used_slots does not match non-empty slots")
        _check_slot_order(self.slots, "inventory")
        return self


class ToolbarState(StrictModel):
    size: int = Field(ge=0, le=12)
    slots: list[InventorySlotState]
    selected_index: int
    selected_item: ItemState | None
    selected_item_id: str | None
    selected_item_is_temporary: bool
    item_stowed: bool

    @model_validator(mode="after")
    def _check(self) -> Self:
        if len(self.slots) != self.size:
            raise ValueError(f"toolbar has {len(self.slots)} slots, size {self.size}")
        _check_slot_order(self.slots, "toolbar")
        if (self.selected_item is None) != (self.selected_item_id is None):
            raise ValueError("toolbar selected_item_id does not match selected_item")
        return self


class EquipmentState(StrictModel):
    hat: ItemState | None
    shirt: ItemState | None
    pants: ItemState | None
    boots: ItemState | None
    left_ring: ItemState | None
    right_ring: ItemState | None
    trinket_slots_unlocked: bool
    trinkets: list[ItemState | None]


# ---- world / player ----------------------------------------------------------------------------


class WorldState(StrictModel):
    year: int = Field(ge=1)
    season: str
    day_of_month: int = Field(ge=1, le=28)
    day_of_week: str
    total_days: int = Field(ge=0)
    # Stardew HHMM clock: 600 (6am) .. 2600 (2am, pass out).
    time_of_day: int = Field(ge=600, le=2600)
    time_text: str
    weather: str | None
    weather_tomorrow: str | None
    is_raining: bool
    is_snowing: bool
    is_lightning: bool
    is_debris_weather: bool
    is_green_rain: bool
    location_context: str | None
    location_name: str
    location_unique_name: str
    location_display_name: str
    map_path: str
    is_outdoors: bool
    is_paused: bool
    time_passing: bool
    is_festival: bool
    event_active: bool

    @model_validator(mode="after")
    def _check(self) -> Self:
        if self.season not in SEASONS:
            raise ValueError(f"unknown season {self.season!r}")
        if self.time_of_day % 100 >= 60:
            raise ValueError(f"invalid time_of_day {self.time_of_day}")
        expected = f"{self.time_of_day // 100:02}:{self.time_of_day % 100:02}"
        if self.time_text != expected:
            raise ValueError(f"time_text {self.time_text!r} != {expected!r}")
        return self


class SkillLevelState(StrictModel):
    base: int = Field(ge=0)
    effective: int


class SkillsState(StrictModel):
    farming: SkillLevelState
    fishing: SkillLevelState
    foraging: SkillLevelState
    mining: SkillLevelState
    combat: SkillLevelState
    luck: SkillLevelState


class PlayerState(StrictModel):
    name: str
    tile_x: int
    tile_y: int
    facing_direction: int = Field(ge=0, le=3)
    facing: str
    # Stamina can go below zero when exhausted.
    stamina: float
    max_stamina: int = Field(ge=0)
    health: int
    max_health: int = Field(ge=0)
    skills: SkillsState

    @model_validator(mode="after")
    def _check(self) -> Self:
        if self.facing != FACING_NAMES[self.facing_direction]:
            raise ValueError(f"facing {self.facing!r} does not match facing_direction {self.facing_direction}")
        return self


# ---- finance / quests --------------------------------------------------------------------------


class FinanceState(StrictModel):
    money: int
    wallet_scope: Literal["shared", "individual"]
    separate_wallets: bool
    total_money_earned: int
    total_money_earned_scope: str
    individual_money_earned: int | None
    shipping_bin_item_count: int = Field(ge=0)
    shipping_bin_estimated_value: int = Field(ge=0)
    shipped_income_today: int | None
    null_reasons: dict[str, str]

    @model_validator(mode="after")
    def _check(self) -> Self:
        if (self.wallet_scope == "individual") != self.separate_wallets:
            raise ValueError("wallet_scope contradicts separate_wallets")
        return self


class QuestProgressState(StrictModel):
    current: int | None
    required: int | None
    subject: str | None


class QuestEntryState(StrictModel):
    kind: Literal["quest"]
    quest_id: str | None
    quest_type: int
    quest_type_name: str
    quest_class: str
    title: str
    description: str | None
    objective: str | None
    completed: bool
    is_daily_quest: bool
    is_timed: bool
    days_left: int | None
    progress: QuestProgressState | None
    target_npc: str | None
    money_reward: int | None
    reward_description: str | None
    can_be_cancelled: bool
    is_hidden: bool


class SpecialOrderObjectiveState(StrictModel):
    description: str
    current: int
    required: int
    completed: bool


class SpecialOrderRewardState(StrictModel):
    type: str
    amount: int | None


class SpecialOrderEntryState(StrictModel):
    kind: Literal["special_order"]
    order_key: str
    order_type: str | None
    title: str
    description: str | None
    status: str
    completed: bool
    days_left: int | None
    due_date_total_days: int
    duration: str
    objectives: list[SpecialOrderObjectiveState]
    rewards: list[SpecialOrderRewardState]


class QuestState(StrictModel):
    quests: list[QuestEntryState]
    special_orders: list[SpecialOrderEntryState]


# ---- snapshot / envelope -----------------------------------------------------------------------

SECTION_NAMES = ("world", "player", "inventory", "toolbar", "equipment", "finance", "quests")


class GameStateSnapshot(StrictModel):
    world_ready: bool
    revision: int = Field(ge=0)
    # C# DateTime: UTC values carry "Z"; default(DateTime) on a fresh not-ready snapshot has no offset.
    captured_at_utc: datetime
    changed_at_utc: datetime
    world: WorldState | None
    player: PlayerState | None
    inventory: InventoryState | None
    toolbar: ToolbarState | None
    equipment: EquipmentState | None
    finance: FinanceState | None
    quests: QuestState | None

    @model_validator(mode="after")
    def _check(self) -> Self:
        present = [name for name in SECTION_NAMES if getattr(self, name) is not None]
        if self.world_ready and len(present) != len(SECTION_NAMES):
            missing = sorted(set(SECTION_NAMES) - set(present))
            raise ValueError(f"world_ready=true but sections are null: {missing}")
        if not self.world_ready and present:
            raise ValueError(f"world_ready=false but sections are present: {present}")
        return self


class ObservationEnvelope(StrictModel):
    """One POST /v1/observations body, built by the bridge's ObservationEnvelope.Build."""

    schema_version: Literal[1]
    # New ID each time a save is loaded; Guid "N" format from the bridge.
    session_id: str = Field(pattern=r"^[A-Za-z0-9_-]{8,64}$")
    # 1, 2, 3, ... within one session.
    sequence: int = Field(ge=1)
    created_at_utc: datetime
    bridge_version: str
    snapshot: GameStateSnapshot
