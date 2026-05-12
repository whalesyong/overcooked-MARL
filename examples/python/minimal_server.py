from pathlib import Path

import thriftpy2
from thriftpy2.rpc import make_server


ROOT = Path(__file__).resolve().parents[2]
GAME_THRIFT = thriftpy2.load(str(ROOT / "common" / "game.thrift"), module_name="game_thrift")


class Handler:
    def __init__(self):
        self._prev_round_time = None

    def getNext(self, output):
        item_count = len(output.items) if output.items else 0
        chef_count = len(output.chefs) if output.chefs else 0
        registry_count = len(output.entityRegistry) if output.entityRegistry else 0
        message_count = len(output.serverMessages) if output.serverMessages else 0
        physics_frames = getattr(output, "physicsFramesElapsed", None)
        since_no_physics = getattr(output, "framesSinceLastNoPhysicsFrame", None)
        last_paused = getattr(output, "lastFramePaused", None)
        next_paused = getattr(output, "nextFramePaused", None)
        round_time = getattr(output, "roundTimeSeconds", None)
        round_remaining = getattr(output, "roundTimeRemainingSeconds", None)

        round_delta = None
        if round_time is not None and self._prev_round_time is not None:
            round_delta = round_time - self._prev_round_time
        if round_time is not None:
            self._prev_round_time = round_time

        print(
            f"frame={output.frameNumber} items={item_count} chefs={chef_count} "
            f"registry={registry_count} messages={message_count} "
            f"physics={physics_frames} sinceNoPhysics={since_no_physics} "
            f"lastPaused={last_paused} nextPaused={next_paused} "
            f"roundTime={round_time} roundRemaining={round_remaining} "
            f"roundDt={round_delta}",
            flush=True,
        )

        response = GAME_THRIFT.InputData()
        response.input = {}
        response.preventInvalidState = False
        return response


def main():
    # thriftpy2 defaults to a 3s client timeout, which is too short while the
    # game is loading a level and temporarily stops issuing frame RPCs.
    server = make_server(
        GAME_THRIFT.Interceptor,
        Handler(),
        "0.0.0.0",
        14455,
        client_timeout=60 * 60 * 1000,
    )
    print("Listening on 0.0.0.0:14455", flush=True)
    server.serve()


if __name__ == "__main__":
    main()
