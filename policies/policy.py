from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, Optional

import thriftpy2


ROOT = Path(__file__).resolve().parents[1]
GAME_THRIFT = thriftpy2.load(str(ROOT / "common" / "game.thrift"), module_name="policy_game_thrift")

# EXPECTED SHAPES: 
"""
struct ButtonInput {
    1: bool down,
    2: bool justPressed,
    3: bool justReleased,
}

struct OneInputData {
    1: PadDirection pad,
    7: ButtonInput pickup,
    8: ButtonInput interact,
    9: ButtonInput dash,
}

struct InputData {
    1: map<i32, OneInputData> input,
    2: i32 resetOrderSeed,
    3: optional double gameSpeed,
    4: optional WarpSpec warp,
    5: optional bool requestPause,
    6: optional bool requestResume,
    7: optional i32 nextFrame,
    8: bool preventInvalidState,
}

struct OutputData {
    1: map<i32, ChefSpecificData> chefs,
    2: map<i32, ItemData> items,
    3: list<ServerMessage> serverMessages,
    4: list<EntityRegistryData> entityRegistry,
    6: bool lastFramePaused,
    7: bool nextFramePaused,
    8: i32 frameNumber,
    9: optional string invalidStateReason,
    10: i32 physicsFramesElapsed,
    11: i32 framesSinceLastNoPhysicsFrame,
    12: optional double roundTimeSeconds,
    13: optional double roundTimeRemainingSeconds,
    14: i32 episodeId,
    15: i32 episodeStep,
    16: bool inEpisode,
    17: bool episodeStart,
    18: bool episodeEnd,
    19: optional string episodeEndReason,
    20: optional string gameState,
    21: optional string levelName,
    22: map<i32, OneInputData> observedInput,
    23: bool fullSnapshot,
    24: list<EntityWarpSpec> entityState,
}



"""


@dataclass
class ButtonAction:
    down: bool = False
    just_pressed: bool = False
    just_released: bool = False


@dataclass
class ChefAction:
    move_x: float = 0.0
    move_y: float = 0.0
    pickup: ButtonAction = field(default_factory=ButtonAction)
    interact: ButtonAction = field(default_factory=ButtonAction)
    dash: ButtonAction = field(default_factory=ButtonAction)


class BasePolicy(ABC):
    """Base class for deterministic bridge policies.

    Subclasses implement `act(output)` and return a mapping from chef entity id
    to `ChefAction`. The base class handles episode lifecycle bookkeeping and
    conversion to bridge `InputData`.
    """

    def __init__(self) -> None:
        self.episode_id: Optional[int] = None
        self.episode_step: Optional[int] = None
        self.level_name: Optional[str] = None

    def reset(self) -> None:
        self.episode_id = None
        self.episode_step = None
        self.level_name = None

    def on_episode_start(self, output: Any) -> None:
        self.episode_id = getattr(output, "episodeId", None)
        self.episode_step = getattr(output, "episodeStep", None)
        self.level_name = getattr(output, "levelName", None)

    def on_episode_end(self, output: Any) -> None:
        self.episode_step = getattr(output, "episodeStep", self.episode_step)

    def update_episode_state(self, output: Any) -> None:
        episode_start = bool(getattr(output, "episodeStart", False))
        episode_end = bool(getattr(output, "episodeEnd", False))
        in_episode = bool(getattr(output, "inEpisode", False))

        if episode_start or (in_episode and self.episode_id != getattr(output, "episodeId", None)):
            self.on_episode_start(output)

        if in_episode:
            self.episode_id = getattr(output, "episodeId", self.episode_id)
            self.episode_step = getattr(output, "episodeStep", self.episode_step)
            self.level_name = getattr(output, "levelName", self.level_name)

        if episode_end:
            self.on_episode_end(output)

    @abstractmethod
    def act(self, output: Any) -> Dict[int, ChefAction]:
        """Return actions keyed by chef entity id for the current frame."""

    def get_next(self, output: Any):
        self.update_episode_state(output)
        actions = self.act(output)
        return self.build_input(actions)

    def build_input(self, actions: Dict[int, ChefAction]):
        response = GAME_THRIFT.InputData()
        response.input = {}
        response.preventInvalidState = False

        for chef_entity_id, action in actions.items():
            response.input[int(chef_entity_id)] = self._build_one_input(action)

        return response

    def idle_response(self):
        return self.build_input({})

    @staticmethod
    def _build_one_input(action: ChefAction):
        one_input = GAME_THRIFT.OneInputData()
        one_input.pad = GAME_THRIFT.PadDirection(x=float(action.move_x), y=float(action.move_y))
        one_input.pickup = BasePolicy._build_button(action.pickup)
        one_input.interact = BasePolicy._build_button(action.interact)
        one_input.dash = BasePolicy._build_button(action.dash)
        return one_input

    @staticmethod
    def _build_button(action: ButtonAction):
        return GAME_THRIFT.ButtonInput(
            down=bool(action.down),
            justPressed=bool(action.just_pressed),
            justReleased=bool(action.just_released),
        )
