#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME_DIR="${GAME_DIR:-/Volumes/Lexar/SteamLibrary/steamapps/common/Overcooked! 2}"
GAME_BIN="$GAME_DIR/Overcooked2.app/Contents/MacOS/Overcooked2"
RUN_SCRIPT="$GAME_DIR/run_bepinex.sh"

PEER_SESSION="${PEER_SESSION:-bridge-peer}"
PEER_CMD="${PEER_CMD:-python3 examples/python/record_dataset.py}"

BRIDGE_PORT="${BRIDGE_PORT:-14455}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-3}"
CONNECT_TIMEOUT_SEC="${CONNECT_TIMEOUT_SEC:-45}"
STABLE_PID_SEC="${STABLE_PID_SEC:-20}"
POST_READY_SEC="${POST_READY_SEC:-15}"
STARTUP_LOG="${STARTUP_LOG:-/tmp/overcooked-bridge-startup.log}"
GAME_STDOUT_LOG="${GAME_STDOUT_LOG:-/tmp/overcooked-bepinex.log}"
BEPINEX_LOG="${BEPINEX_LOG:-$GAME_DIR/BepInEx/LogOutput.log}"
STEAM_APP_ID="${STEAM_APP_ID:-728880}"
AUTO_STEAM_APPID="${AUTO_STEAM_APPID:-1}"

timestamp() {
  date "+%Y-%m-%d %H:%M:%S"
}

log() {
  local msg="$*"
  echo "[$(timestamp)] [bridge-launch] $msg" | tee -a "$STARTUP_LOG"
}

require_cmd() {
  local name="$1"
  if ! command -v "$name" >/dev/null 2>&1; then
    echo "Missing required command: $name" >&2
    exit 1
  fi
}

kill_game_processes() {
  local pids
  pids="$(pgrep -f "$GAME_BIN" || true)"
  if [[ -z "$pids" ]]; then
    log "no running game process found"
    return
  fi

  log "killing existing game pids: $pids"
  kill $pids || true
  sleep 1

  pids="$(pgrep -f "$GAME_BIN" || true)"
  if [[ -n "$pids" ]]; then
    log "forcing kill for remaining game pids: $pids"
    kill -9 $pids || true
  fi
}

stop_peer_session() {
  if ! tmux has-session -t "$PEER_SESSION" >/dev/null 2>&1; then
    return
  fi

  log "stopping existing peer tmux session: $PEER_SESSION"
  tmux send-keys -t "$PEER_SESSION:0.0" C-c >/dev/null 2>&1 || true
  for _ in {1..5}; do
    if ! tmux has-session -t "$PEER_SESSION" >/dev/null 2>&1; then
      return
    fi
    sleep 1
  done

  log "peer did not exit after interrupt; killing tmux session: $PEER_SESSION"
  tmux kill-session -t "$PEER_SESSION" >/dev/null 2>&1 || true
}

restart_peer_session() {
  log "restarting peer tmux session: $PEER_SESSION"
  stop_peer_session
  tmux new-session -d -s "$PEER_SESSION" "cd \"$ROOT_DIR\" && $PEER_CMD; status=\$?; echo \"[bridge-peer] exited with status \$status\"; sleep 300"
  sleep 1
  tmux capture-pane -pt "$PEER_SESSION:0.0" | tail -n 5 | tee -a "$STARTUP_LOG"
}

launch_game_once() {
  ensure_steam_appid_files
  log "launching game with run_bepinex.sh"
  (
    cd "$GAME_DIR"
    nohup env SteamAppId="$STEAM_APP_ID" SteamGameId="$STEAM_APP_ID" ./run_bepinex.sh >"$GAME_STDOUT_LOG" 2>&1 &
  )
}

ensure_steam_appid_files() {
  if [[ "$AUTO_STEAM_APPID" != "1" ]]; then
    return
  fi

  local paths=(
    "$GAME_DIR/steam_appid.txt"
    "$GAME_DIR/Overcooked2.app/Contents/MacOS/steam_appid.txt"
  )
  local path=""
  for path in "${paths[@]}"; do
    local current=""
    if [[ -f "$path" ]]; then
      current="$(tr -d '[:space:]' < "$path" || true)"
    fi
    if [[ "$current" == "$STEAM_APP_ID" ]]; then
      continue
    fi
    printf '%s\n' "$STEAM_APP_ID" > "$path"
    log "wrote steam app id to $path"
  done
}

connected_to_peer() {
  lsof -nP -iTCP:"$BRIDGE_PORT" 2>/dev/null | awk 'NR > 1 && /ESTABLISHED/ { found = 1 } END { exit found ? 0 : 1 }'
}

peer_received_frames() {
  tmux capture-pane -pt "$PEER_SESSION:0.0" 2>/dev/null | grep -Eq '^(frame=|recorded recv=|Recording episode |poll recv=)'
}

peer_exited() {
  tmux capture-pane -pt "$PEER_SESSION:0.0" 2>/dev/null | grep -q '^\[bridge-peer\] exited with status '
}

wait_for_connection() {
  local deadline=$((SECONDS + CONNECT_TIMEOUT_SEC))
  local last_pid=""
  local current_pid=""
  local last_pid_change=$SECONDS
  local saw_bridge_signal=0
  local ready_since=0
  local ready_pid=""

  while (( SECONDS < deadline )); do
    current_pid="$(pgrep -f "$GAME_BIN" | head -n 1 || true)"
    if [[ "$current_pid" != "$last_pid" ]]; then
      log "game pid transition: ${last_pid:-none} -> ${current_pid:-none}"
      last_pid="$current_pid"
      last_pid_change=$SECONDS
      saw_bridge_signal=0
      ready_since=0
      ready_pid=""
    fi

    if connected_to_peer; then
      saw_bridge_signal=1
    fi

    if peer_received_frames; then
      saw_bridge_signal=1
    fi

    if peer_exited; then
      log "peer process exited before bridge connection"
      tmux capture-pane -pt "$PEER_SESSION:0.0" 2>/dev/null | tail -n 20 | tee -a "$STARTUP_LOG"
      return 1
    fi

    if [[ "$saw_bridge_signal" == "1" && -n "$current_pid" ]] && (( SECONDS - last_pid_change >= STABLE_PID_SEC )); then
      if (( ready_since == 0 )); then
        ready_since=$SECONDS
        ready_pid="$current_pid"
        log "bridge provisional ready; holding ${POST_READY_SEC}s for stability (pid=$current_pid)"
      fi
    fi

    if (( ready_since > 0 )); then
      if [[ -z "$current_pid" || "$current_pid" != "$ready_pid" ]]; then
        log "bridge readiness invalidated by pid change during post-ready hold"
        ready_since=0
        ready_pid=""
      elif (( SECONDS - ready_since >= POST_READY_SEC )); then
        log "bridge ready (pid stable for ${STABLE_PID_SEC}s + post-ready hold ${POST_READY_SEC}s) pid=$current_pid"
        return 0
      fi
    fi
    sleep 1
  done

  log "timed out waiting for bridge TCP connection"
  return 1
}

main() {
  : >"$STARTUP_LOG"
  require_cmd tmux
  require_cmd lsof
  require_cmd pgrep
  if [[ ! -x "$RUN_SCRIPT" ]]; then
    echo "Missing or non-executable run script: $RUN_SCRIPT" >&2
    exit 1
  fi

  log "startup parameters: GAME_DIR=$GAME_DIR BRIDGE_PORT=$BRIDGE_PORT MAX_ATTEMPTS=$MAX_ATTEMPTS CONNECT_TIMEOUT_SEC=$CONNECT_TIMEOUT_SEC STABLE_PID_SEC=$STABLE_PID_SEC POST_READY_SEC=$POST_READY_SEC"

  local attempt
  for ((attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)); do
    log "attempt $attempt/$MAX_ATTEMPTS"
    restart_peer_session
    kill_game_processes
    if [[ -f "$BEPINEX_LOG" ]]; then
      rm -f "$BEPINEX_LOG"
    fi
    launch_game_once
    if wait_for_connection; then
      log "bridge startup successful"
      exit 0
    fi
    if [[ -f "$BEPINEX_LOG" ]] && grep -q "TASPatcher.Shutdown from OnApplicationQuit" "$BEPINEX_LOG"; then
      log "detected Steam relaunch handoff (plugin quit path seen before stable bridge connect)"
    fi
  done

  log "bridge startup failed after $MAX_ATTEMPTS attempts"
  exit 1
}

main "$@"
