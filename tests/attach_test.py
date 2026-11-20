import argparse
from pathlib import Path
import subprocess
import tempfile
import time
import unittest

import protocol_test
from smoke import Client


class AttachTests(unittest.TestCase):
    def test_detach_keeps_the_target_running_and_resumed(self):
        with tempfile.TemporaryDirectory() as directory:
            heartbeat = Path(directory) / "heartbeat.txt"
            target = subprocess.Popen([str(protocol_test.FIXTURE), "--heartbeat", str(heartbeat)])
            try:
                deadline = time.monotonic() + 5
                while not heartbeat.exists():
                    if time.monotonic() >= deadline:
                        self.fail("Target did not start its heartbeat")
                    time.sleep(0.02)
                for eof in (False, True):
                    c = Client(protocol_test.ADAPTER)
                    try:
                        caps = c.request("initialize")
                        self.assertTrue(caps["supportTerminateDebuggee"])
                        attach = c.send("attach", processId=target.pid)
                        c.event("initialized")
                        c.request("configurationDone")
                        c.response(attach)
                        c.event("thread")
                        c.request("pause")
                        stop = c.event("stopped")
                        c.request("stackTrace", threadId=stop["threadId"])
                        if eof:
                            c.proc.stdin.close()
                        else:
                            c.request("disconnect")
                        self.assertEqual(c.proc.wait(timeout=5), 0,
                                         f"target exit={target.poll()}, EOF={eof}, messages={c.messages}")
                    finally:
                        c.close()
                    before = heartbeat.stat().st_mtime_ns
                    deadline = time.monotonic() + 3
                    while heartbeat.stat().st_mtime_ns == before:
                        if time.monotonic() >= deadline:
                            self.fail("Attached target did not resume after disconnect")
                        time.sleep(0.02)
                    self.assertIsNone(target.poll())
            finally:
                if target.poll() is None:
                    target.terminate()
                target.wait(timeout=5)

    def test_explicit_termination_only_terminates_the_selected_target(self):
        target = subprocess.Popen([str(protocol_test.FIXTURE), "--wait"])
        other = subprocess.Popen([str(protocol_test.FIXTURE), "--wait"])
        try:
            c = Client(protocol_test.ADAPTER)
            self.addCleanup(c.close)
            c.request("initialize")
            attach = c.send("attach", processId=target.pid)
            c.event("initialized")
            c.request("configurationDone")
            c.response(attach)
            c.event("thread")
            c.request("disconnect", terminateDebuggee=True)
            self.assertEqual(c.proc.wait(timeout=5), 0)
            target.wait(timeout=5)
            self.assertIsNone(other.poll())
        finally:
            for proc in (target, other):
                if proc.poll() is None:
                    proc.terminate()
                proc.wait(timeout=5)

    def test_target_exit_racing_eof_is_clean(self):
        target = subprocess.Popen([str(protocol_test.FIXTURE), "--wait"])
        try:
            c = Client(protocol_test.ADAPTER)
            self.addCleanup(c.close)
            c.request("initialize")
            attach = c.send("attach", processId=target.pid)
            c.event("initialized")
            c.request("configurationDone")
            c.response(attach)
            c.event("thread")
            c.request("pause")
            c.event("stopped")
            target.terminate()
            c.proc.stdin.close()
            self.assertEqual(c.proc.wait(timeout=5), 0)
        finally:
            if target.poll() is None:
                target.terminate()
            target.wait(timeout=5)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", type=Path, default=protocol_test.ADAPTER)
    parser.add_argument("--fixture", type=Path, default=protocol_test.FIXTURE)
    args, remaining = parser.parse_known_args()
    protocol_test.ADAPTER, protocol_test.FIXTURE = args.adapter.resolve(), args.fixture.resolve()
    unittest.main(argv=[__file__, *remaining])
