"""File-based GPU Admission client for a cooperating Linux/Docker worker."""
import argparse
import json
import os
from pathlib import Path
import re
import socket
import tempfile
import time
import uuid

SCHEMAS = ("home-network-gpu-admission/v1", "gpu-admission/v2")


def read(path):
    try:
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        with os.fdopen(fd, "rb") as stream:
            data = stream.read(16385)
        value = json.loads(data, strict=True) if len(data) <= 16384 else None
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def fresh(record):
    observed = record.get("observed_at")
    now = time.time()
    return type(observed) in (int, float) and now - 15 <= observed <= now


def replace(path, record):
    path.parent.mkdir(exist_ok=True)
    fd, temporary = tempfile.mkstemp(dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(record, stream, allow_nan=False)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


class Worker:
    def __init__(self, status_directory, request_directory, worker):
        if not re.fullmatch(r"[a-z][a-z0-9-]{0,47}", worker):
            raise ValueError("invalid worker ID")
        self.public = Path(status_directory)
        self.requests = Path(request_directory)
        self.worker = worker

    def status(self):
        return read(self.public / "status.json")

    def eligible(self, status):
        modes = status.get("consumer_modes")
        gpu, generation = status.get("gpu"), status.get("generation")
        return (fresh(status) and status.get("schema") in SCHEMAS
                and isinstance(gpu, str) and bool(gpu)
                and isinstance(generation, str) and re.fullmatch(r"[0-9a-f]{32}", generation) is not None
                and status.get("policy_mode") == "Auto" and isinstance(modes, dict)
                and modes.get(self.worker) == "Auto")

    def announce(self, status=None, phase="idle", session=None):
        status = self.status() if status is None else status
        modes = status.get("consumer_modes")
        if not (fresh(status) and status.get("schema") in SCHEMAS
                and isinstance(modes, dict) and self.worker in modes):
            return False
        try:
            replace(self.requests / "presence" / "heartbeat.json", {
                "schema": "gpu-admission-presence/v1", "consumer": self.worker,
                "session_id": session or uuid.uuid4().hex, "generation": status.get("generation"),
                "phase": phase, "observed_at": time.time()
            })
            return True
        except OSError:
            return False

    def acquire(self, minimum_free_vram_mib=0, timeout=600):
        """Wait for an active grant. No workload is launched by this method."""
        if type(minimum_free_vram_mib) not in (int, float) or not 0 <= minimum_free_vram_mib <= 262144:
            raise ValueError("minimum free memory must be between 0 and 262144 MiB")
        until = time.monotonic() + timeout
        lease = None
        try:
            while time.monotonic() < until:
                status = self.status()
                if self.eligible(status):
                    if lease is None:
                        lease = Lease(self, status, minimum_free_vram_mib)
                    if not all(status.get(k) == lease.record[k] for k in ("schema", "gpu", "generation")):
                        break
                    if lease.matches(status, "reserved"):
                        lease.record["state"] = "active"
                    lease.heartbeat()
                    if lease.record["state"] == "active" and lease.matches(status, "active"):
                        ready, lease = lease, None
                        return ready
                elif lease is not None:
                    break  # No workload started. Release safely when admission changes.
                time.sleep(1)
        finally:
            if lease:
                lease.release()  # acquire never starts the job.
        return None


class Lease:
    def __init__(self, worker, status, memory):
        self.worker = worker
        self.record = {key: status[key] for key in ("schema", "gpu", "generation")}
        self.record.update(consumer=worker.worker, request_id=uuid.uuid4().hex,
                           state="requested", minimum_free_vram_mib=memory)
        hostname = socket.gethostname()
        if re.fullmatch(r"(?:[0-9a-f]{12}|[0-9a-f]{64})", hostname):
            self.record["docker_instance"] = hostname
        self.path = worker.requests / (self.record["request_id"] + ".json")
        self.running = False
        self.released = False

    def matches(self, status, phase):
        grant = read(self.worker.public / "grants" / (self.worker.worker + ".json"))
        keys = ("schema", "gpu", "generation", "consumer", "request_id")
        return (self.worker.eligible(status) and fresh(grant) and grant.get("phase") == phase
                and all(status.get(k) == self.record[k] == grant.get(k) for k in keys))

    def can_start(self):
        return not self.running and not self.released and self.record["state"] == "active" and self.matches(self.worker.status(), "active")

    def mark_running(self):
        """Call immediately after starting the workload."""
        if self.released or self.record["state"] != "active":
            raise RuntimeError("cannot report an unenrolled workload")
        self.running = True
        self.heartbeat()

    def heartbeat(self):
        """Call at least every five seconds until the job has fully stopped."""
        if self.released:
            return
        now = int(time.time())
        self.record.update(observed_at=now, heartbeat_at=now)
        replace(self.path, self.record)
        self.worker.announce(phase="running" if self.running else "waiting", session=self.record["request_id"])

    def release(self):
        """Call only when no part of this GPU workload is still running."""
        if self.released:
            return
        self.record["state"] = "released"
        now = int(time.time())
        self.record.update(observed_at=now, heartbeat_at=now)
        replace(self.path, self.record)
        self.released = True
        self.worker.announce()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Test a worker connection while admission is held.")
    parser.add_argument("action", choices=["probe"])
    parser.add_argument("status_directory")
    parser.add_argument("request_directory")
    parser.add_argument("worker_id")
    args = parser.parse_args()
    worker = Worker(args.status_directory, args.request_directory, args.worker_id)
    if not worker.announce():
        parser.exit(3, "No fresh guard status for this registered worker. Check the read mount.\n")
    print("Connection verified: read guard status and wrote worker presence.")
