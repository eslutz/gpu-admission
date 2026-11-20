"""Contract checks for the Linux worker client, including real bind mounts when supplied."""
import os
from pathlib import Path
import tempfile
import threading
import time
import unittest

from gpu_admission_client import SCHEMAS, Worker, read, replace


class ClientTest(unittest.TestCase):
    def setUp(self):
        mount = os.environ.get("GPU_ADMISSION_TEST_BIND")
        self.bound = bool(mount)
        self.temp = None if self.bound else tempfile.TemporaryDirectory()
        self.root = Path(mount or self.temp.name)
        for name in ("public", "requests", "public/grants"):
            (self.root / name).mkdir(exist_ok=True)
        self.status = self.root / "public/status.json"
        self.grant = self.root / "public/grants/worker-a.json"
        self.worker = Worker("/bound-public" if self.bound else self.root / "public",
                             "/bound-requests" if self.bound else self.root / "requests", "worker-a")
        self.data = dict(schema="home-network-gpu-admission/v1", generation="1" * 32,
                         gpu="GPU-01234567-0123-0123-0123-0123456789ab",
                         policy_mode="Hold", consumer_modes={"worker-a": "Auto"})
        self.publish()

    def tearDown(self):
        if self.temp:
            self.temp.cleanup()

    def publish(self, age=0):
        self.data["observed_at"] = int(time.time()) - age
        replace(self.status, self.data)

    def until(self, response, condition):
        stop = time.monotonic() + 4
        while time.monotonic() < stop:
            value = response()
            if condition(value):
                return value
            time.sleep(0.04)
        self.fail("worker fixture did not reach the expected state")

    def test_round_trip(self):
        self.assertEqual(len(SCHEMAS), 2)
        self.assertTrue(self.worker.announce())
        presence = self.root / "requests/presence/heartbeat.json"
        self.assertEqual(read(presence)["phase"], "idle")
        self.assertFalse(self.worker.eligible(self.worker.status()))
        # Docker Desktop bind mounts may follow the host's case rules.
        # Assert exact casing on the Linux filesystem.
        if not self.bound:
            self.assertFalse(Worker(self.root / "Public", self.root / "requests", "worker-a").announce())
            (self.root / "status-symlink").symlink_to(self.status)
            self.assertFalse(read(self.root / "status-symlink"))
        if self.bound:
            with self.assertRaises(OSError):
                replace(self.worker.public / "write-should-fail.json", {})
        self.data["policy_mode"] = "Auto"
        self.publish(age=16)
        self.assertFalse(self.worker.eligible(self.worker.status()))
        self.publish()
        leases = []
        acquire = threading.Thread(target=lambda: leases.append(self.worker.acquire(timeout=9)))
        acquire.start()
        requests = self.root / "requests"
        path = self.until(lambda: list(requests.glob("*.json")), bool)[0]
        request = read(path)
        self.assertEqual(request["state"], "requested")
        self.assertIsInstance(request["observed_at"], int)
        self.data.update(consumer="worker-a", request_id=request["request_id"])
        grant = dict(request, phase="reserved")
        self.publish()
        replace(self.grant, dict(grant, consumer="worker-b", observed_at=int(time.time())))
        time.sleep(1.15)
        self.assertEqual(read(path)["state"], "requested")
        replace(self.grant, dict(grant, observed_at=int(time.time())))
        self.until(lambda: read(path), lambda obj: obj.get("state") == "active")
        self.assertTrue(acquire.is_alive())
        replace(self.grant, dict(grant, phase="active", observed_at=int(time.time())))
        acquire.join(5)
        self.assertFalse(acquire.is_alive())
        lease = leases[0]
        self.assertTrue(lease.can_start())
        lease.mark_running()  # Synthetic workload; the fixture starts no GPU commands.
        self.assertFalse(lease.can_start())
        self.assertEqual(read(presence)["phase"], "running")
        self.data["policy_mode"] = "Hold"
        self.publish()
        lease.release()
        self.assertEqual(read(path)["state"], "released")
        self.assertEqual(read(presence)["phase"], "idle")

    def test_pause_releases_unstarted_request(self):
        self.data["policy_mode"] = "Auto"
        self.publish()
        leases = []
        acquire = threading.Thread(target=lambda: leases.append(self.worker.acquire(timeout=9)))
        acquire.start()
        paths = self.root / "requests"
        path = self.until(lambda: list(paths.glob("*.json")), bool)[0]
        self.data["policy_mode"] = "Hold"
        self.publish()
        acquire.join(5)
        self.assertEqual(leases, [None])
        self.assertEqual(read(path)["state"], "released")


if __name__ == "__main__":
    unittest.main()
