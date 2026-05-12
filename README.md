# Overcooked 2 BepInEx Bridge

This repo is being trimmed down to a minimal bridge between Overcooked 2 and an external process.

The active deliverable is the `patch/` project:
- BepInEx loads a plugin into the game.
- Harmony patches capture game state and intercept player input.
- The plugin sends `OutputData` to an external Thrift peer and receives `InputData` back.

The protocol remains defined in `common/game.thrift`.
The generated C# Thrift sources under `patch/gen-csharp/` are part of the bridge and should be kept in the repo.

## Scope

The bridge keeps the parts needed for:
- per-frame state capture
- entity registry snapshots
- game server message capture
- external action injection

It intentionally drops controller UI concerns from the build path, including the TAS web app, warping, debug overlays, and invalid-state tooling.

`controller/` is still present in the repository as legacy reference code, but it is no longer part of the root solution or the active bridge build.

## Build

Requirements:
- .NET Framework 3.5 SDK/runtime support for the patch build
- BepInEx 5
- the game and Unity DLLs listed in `patch/Libs/README.md`

Build the plugin with:

```bash
dotnet build patch/SuperchargedPatch.csproj -c Release
```

## Install

1. Install BepInEx 5 into the Overcooked 2 game directory.
2. Copy the DLLs listed in `patch/Libs/README.md` into `patch/Libs` before building.
3. Build `patch/SuperchargedPatch.csproj` in `Release` mode.
4. Copy these files into `BepInEx/plugins`:
   - `SuperchargedPatch.dll`
   - `ApacheThrift.dll`
   - `Newtonsoft.Json.dll`

## Runtime Model

The plugin is a TCP client. It connects to an external service on port `14455` and calls this Thrift RPC once per frame:

```thrift
service Interceptor {
    InputData getNext(1: OutputData output),
}
```

Conceptually:
- game -> external process: `OutputData`
- external process -> game: `InputData`

Useful payloads in `OutputData`:
- `items`: live per-entity position, rotation, and velocity updates
- `chefs`: chef-specific gameplay state
- `entityRegistry`: metadata describing what entities are
- `serverMessages`: raw game messages captured from Team17's networking layer

## Recording Peer

The default peer is `examples/python/record_dataset.py`. It writes one compressed JSONL trajectory file per episode.

Install its dependency with:

```bash
pip install -r examples/python/requirements.txt
```

Choose an output directory, ideally on external storage:

```bash
export RECORD_DIR=/Volumes/YourDrive/overcooked-recordings
./scripts/run_bridge_session.sh
```

The recorder creates `run_<timestamp>_<pid>/run_metadata.json` and episode files named like `episode_000001_<level>_<timestamp>.jsonl.zst`. Each line is an event record with the raw `OutputData` payload, episode metadata, timers, entity deltas/full snapshots, server messages, and observed human inputs. If the game stops without an explicit episode end, the recorder closes the episode after `RECORD_IDLE_END_SEC=30` seconds by default.

A minimal debug peer still lives at `examples/python/minimal_server.py`. To use it instead:

```bash
PEER_CMD="python3 examples/python/minimal_server.py" ./scripts/run_bridge_session.sh
```

## Bridge Session Helper

To restart the peer and game together with startup diagnostics:

```bash
./scripts/run_bridge_session.sh
```

This helper:
- restarts the `bridge-peer` tmux session,
- kills any existing game process,
- starts the recording peer by default,
- launches `run_bepinex.sh` with `SteamAppId/SteamGameId`,
- auto-writes `steam_appid.txt` (appid `728880`) into the game root and app binary folder,
- waits until a live bridge TCP connection or streamed `frame=` output is observed, then applies a post-ready hold before returning success.

Startup traces are written to `/tmp/overcooked-bridge-startup.log`, and game launcher stdout goes to `/tmp/overcooked-bepinex.log`.
The readiness gate requires a stable game PID for `20` seconds by default (`STABLE_PID_SEC` override available), then an additional `15` second post-ready hold (`POST_READY_SEC` override).

## Key Files

- `patch/TASPatcher.cs`: BepInEx entrypoint
- `patch/Injection/InjectorServer.cs`: Thrift transport loop
- `patch/ControllerHandler.cs`: per-frame orchestration
- `patch/ActiveStateCollector.cs`: state capture
- `patch/AlteredComponents/InputPatching.cs`: game input interception
- `patch/TASLogicalButton.cs`: external input injection
- `patch/AlteredComponents/SynchronizationPatches.cs`: entity registry and server message capture
- `common/game.thrift`: wire protocol
