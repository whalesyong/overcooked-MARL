import base64
import json
import os
import queue
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

import thriftpy2
from thriftpy2.rpc import make_server

try:
    import zstandard as zstd
except ImportError:
    zstd = None


ROOT = Path(__file__).resolve().parents[2]
GAME_THRIFT = thriftpy2.load(str(ROOT / "common" / "game.thrift"), module_name="game_thrift")


def utc_stamp():
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def local_now():
    return datetime.now().astimezone()


def local_stamp(dt=None):
    if dt is None:
        dt = local_now()
    return dt.strftime("%Y%m%dT%H%M%S")


def local_iso(dt=None):
    if dt is None:
        dt = local_now()
    return dt.isoformat(timespec="seconds")


def local_stamp_from_ns(timestamp_ns):
    dt = datetime.fromtimestamp(timestamp_ns / 1_000_000_000, tz=timezone.utc).astimezone()
    return local_stamp(dt)


def safe_name(value):
    value = value or "unknown"
    value = re.sub(r"[^A-Za-z0-9_.-]+", "_", str(value)).strip("_")
    return value or "unknown"


def to_jsonable(value):
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    if isinstance(value, (bytes, bytearray)):
        return {"__binary_b64": base64.b64encode(bytes(value)).decode("ascii")}
    if isinstance(value, dict):
        return {str(k): to_jsonable(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [to_jsonable(v) for v in value]
    if hasattr(value, "__dict__"):
        return {
            key: to_jsonable(item)
            for key, item in value.__dict__.items()
            if not key.startswith("_") and item is not None
        }
    return str(value)


class EpisodeWriter:
    def __init__(self, run_dir):
        self.run_dir = run_dir
        self.queue = queue.Queue(maxsize=20000)
        self.current_episode_id = None
        self.current_path = None
        self.raw_file = None
        self.zstd_writer = None
        self.zstd_process = None
        self.output_stream = None
        self.thread = threading.Thread(target=self._run, name="episode-writer", daemon=True)
        self.thread.start()

    def submit(self, record):
        try:
            self.queue.put_nowait(record)
        except queue.Full:
            print("Recorder queue full; dropping frame", flush=True)

    def close(self):
        self.queue.put(None)
        self.thread.join(timeout=10)

    def preview_episode_path(self, episode_id, level_name, timestamp_ns):
        stamp = local_stamp_from_ns(timestamp_ns)
        filename = f"episode_{episode_id:06d}_{safe_name(level_name)}_{stamp}.jsonl.zst"
        return self.run_dir / filename

    def _open_episode(self, record):
        episode_id = record.get("episode_id", 0)
        preview_path = record.get("_episode_path")
        self.current_path = Path(preview_path) if preview_path is not None else self.preview_episode_path(
            episode_id,
            record.get("level_name"),
            record.get("wall_time_ns", time.time_ns()),
        )
        self.raw_file = self.current_path.open("wb")
        if zstd is not None:
            compressor = zstd.ZstdCompressor(level=3)
            self.zstd_writer = compressor.stream_writer(self.raw_file)
            self.output_stream = self.zstd_writer
        else:
            zstd_bin = shutil.which("zstd")
            if zstd_bin is None:
                raise RuntimeError("Install zstandard (`pip install -r examples/python/requirements.txt`) or install the `zstd` CLI")
            self.zstd_process = subprocess.Popen(
                [zstd_bin, "-q", "-T0", "-c"],
                stdin=subprocess.PIPE,
                stdout=self.raw_file,
            )
            self.output_stream = self.zstd_process.stdin
        self.current_episode_id = episode_id

    def _close_episode(self):
        if self.zstd_writer is not None:
            self.zstd_writer.flush(zstd.FLUSH_FRAME)
            self.zstd_writer.close()
        if self.zstd_process is not None:
            self.zstd_process.stdin.close()
            self.zstd_process.wait(timeout=10)
        if self.raw_file is not None:
            self.raw_file.close()
        self.zstd_writer = None
        self.zstd_process = None
        self.output_stream = None
        self.raw_file = None
        self.current_episode_id = None
        self.current_path = None

    def _write_record(self, record):
        if record["type"] == "episode_start":
            if self.output_stream is not None:
                self._close_episode()
            self._open_episode(record)

        if self.output_stream is None:
            return

        serializable_record = {
            key: value for key, value in record.items() if not key.startswith("_")
        }
        self.output_stream.write(json.dumps(serializable_record, separators=(",", ":"), sort_keys=True).encode("utf-8"))
        self.output_stream.write(b"\n")
        if self.zstd_writer is not None:
            self.zstd_writer.flush(zstd.FLUSH_BLOCK)
        else:
            self.output_stream.flush()

        if record["type"] == "episode_end":
            self._close_episode()

    def _run(self):
        while True:
            record = self.queue.get()
            if record is None:
                self._close_episode()
                return
            try:
                self._write_record(record)
            except Exception as exc:
                print(f"Recorder write failed: {exc}", flush=True)


class Handler:
    def __init__(self, writer):
        self.writer = writer
        self.recv_index = 0
        self.current_episode_id = None
        self.current_level_name = None
        self.current_episode_step = None
        self.idle_poll_count = 0
        self.last_idle_signature = None
        self.last_episode_end_reason = None
        self.last_frame_wall_time = time.time()
        self.idle_end_seconds = float(os.environ.get("RECORD_IDLE_END_SEC", "30"))
        self.lock = threading.Lock()
        self.idle_thread = threading.Thread(target=self._idle_watch, name="episode-idle-watch", daemon=True)
        self.idle_thread.start()

    def getNext(self, output):
        now_ns = time.time_ns()
        now = time.time()
        self.recv_index += 1

        episode_id = getattr(output, "episodeId", None) or 0
        episode_step = getattr(output, "episodeStep", None)
        level_name = getattr(output, "levelName", None)
        game_state = getattr(output, "gameState", None)
        episode_start = bool(getattr(output, "episodeStart", False))
        episode_end = bool(getattr(output, "episodeEnd", False))
        episode_end_reason = getattr(output, "episodeEndReason", None)
        in_episode = bool(getattr(output, "inEpisode", False))

        with self.lock:
            self.last_frame_wall_time = now
            if episode_start or (in_episode and self.current_episode_id != episode_id):
                episode_path = self.writer.preview_episode_path(episode_id, level_name, now_ns)
                print(
                    f"episode start recv={self.recv_index} episode={episode_id} step={episode_step} "
                    f"rawState={game_state} level={level_name} file={episode_path}",
                    flush=True,
                )
                self.current_episode_id = episode_id
                self.current_level_name = level_name
                self.current_episode_step = episode_step
                self.writer.submit(self._record("episode_start", output, now_ns, episode_path=str(episode_path)))
                self.last_episode_end_reason = None
            elif in_episode:
                self.current_level_name = level_name
                self.current_episode_step = episode_step

        if in_episode or episode_start or self.current_episode_id == episode_id:
            self.writer.submit(self._record("frame", output, now_ns))

        if episode_end:
            print(
                f"episode end recv={self.recv_index} episode={episode_id} step={episode_step} "
                f"reason={episode_end_reason} rawState={game_state} level={level_name}",
                flush=True,
            )
            self.writer.submit(self._record("episode_end", output, now_ns))
            with self.lock:
                self.current_episode_id = None
                self.current_level_name = None
                self.current_episode_step = None
                self.last_episode_end_reason = episode_end_reason

        if in_episode or episode_start or episode_end:
            self.idle_poll_count = 0
            self.last_idle_signature = None
        else:
            self.idle_poll_count += 1
            signature = (game_state, level_name, self.last_episode_end_reason)
            if signature != self.last_idle_signature or self.idle_poll_count % 10 == 0:
                print(
                    f"poll recv={self.recv_index} phase=idle rawState={game_state} "
                    f"level={level_name} after={self.last_episode_end_reason}",
                    flush=True,
                )
            self.last_idle_signature = signature

        if in_episode and self.recv_index % 300 == 0:
            print(
                f"recorded recv={self.recv_index} episode={episode_id} step={episode_step} "
                f"level={level_name} inEpisode={in_episode}",
                flush=True,
            )

        response = GAME_THRIFT.InputData()
        response.input = {}
        response.preventInvalidState = False
        return response

    def _record(self, record_type, output, now_ns, episode_path=None):
        record = {
            "type": record_type,
            "wall_time_ns": now_ns,
            "recv_index": self.recv_index,
            "episode_id": getattr(output, "episodeId", None),
            "episode_step": getattr(output, "episodeStep", None),
            "sim_frame": getattr(output, "frameNumber", None),
            "level_name": getattr(output, "levelName", None),
            "game_state": getattr(output, "gameState", None),
            "payload": to_jsonable(output),
        }
        if episode_path is not None:
            record["_episode_path"] = episode_path
        return record

    def _idle_watch(self):
        while True:
            time.sleep(1)
            with self.lock:
                if self.current_episode_id is None:
                    continue
                elapsed = time.time() - self.last_frame_wall_time
                if elapsed < self.idle_end_seconds:
                    continue

                record = {
                    "type": "episode_end",
                    "wall_time_ns": time.time_ns(),
                    "recv_index": self.recv_index,
                    "episode_id": self.current_episode_id,
                    "episode_step": self.current_episode_step,
                    "sim_frame": None,
                    "level_name": self.current_level_name,
                    "game_state": "idle_timeout",
                    "payload": {
                        "episodeId": self.current_episode_id,
                        "episodeStep": self.current_episode_step,
                        "levelName": self.current_level_name,
                        "episodeEnd": True,
                        "episodeEndReason": f"idle_timeout_{self.idle_end_seconds:g}s",
                    },
                }
                self.current_episode_id = None
                self.current_level_name = None
                self.current_episode_step = None
            self.writer.submit(record)

    def force_end_current_episode(self, reason):
        with self.lock:
            if self.current_episode_id is None:
                return
            record = {
                "type": "episode_end",
                "wall_time_ns": time.time_ns(),
                "recv_index": self.recv_index,
                "episode_id": self.current_episode_id,
                "episode_step": self.current_episode_step,
                "sim_frame": None,
                "level_name": self.current_level_name,
                "game_state": reason,
                "payload": {
                    "episodeId": self.current_episode_id,
                    "episodeStep": self.current_episode_step,
                    "levelName": self.current_level_name,
                    "episodeEnd": True,
                    "episodeEndReason": reason,
                },
            }
            self.current_episode_id = None
            self.current_level_name = None
            self.current_episode_step = None
        self.writer.submit(record)


def write_run_metadata(run_dir):
    local_created = local_now()
    metadata = {
        "created_utc": utc_stamp(),
        "created_local": local_iso(local_created),
        "local_timezone": local_created.tzname(),
        "record_dir": str(run_dir),
        "schema": "overcooked_bridge_jsonl_zst_v1",
        "repo_root": str(ROOT),
        "pid": os.getpid(),
    }
    (run_dir / "run_metadata.json").write_text(json.dumps(metadata, indent=2, sort_keys=True) + "\n")


def main():
    base_dir = Path(os.environ.get("RECORD_DIR", ROOT / "recordings")).expanduser()
    run_dir = base_dir / f"run_{local_stamp()}_{os.getpid()}"
    run_dir.mkdir(parents=True, exist_ok=True)
    write_run_metadata(run_dir)

    writer = EpisodeWriter(run_dir)
    handler = Handler(writer)

    def stop(_signum=None, _frame=None):
        handler.force_end_current_episode("recorder_shutdown")
        writer.close()
        sys.exit(0)

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    server = make_server(
        GAME_THRIFT.Interceptor,
        handler,
        "0.0.0.0",
        14455,
        client_timeout=60 * 60 * 1000,
    )
    print(f"Recording bridge data under {run_dir}", flush=True)
    server.serve()


if __name__ == "__main__":
    main()
