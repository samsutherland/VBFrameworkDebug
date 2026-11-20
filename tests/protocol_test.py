import argparse
import ctypes
import json
from pathlib import Path
import unittest

from smoke import Client, ROOT, SOURCE, MARKERS


ADAPTER = ROOT / "bin" / "x64" / "DesktopClr.Adapter.exe"
FIXTURE = ROOT / "dist" / "fixtures" / "x64-portable" / "Fixture.exe"


class ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.client = Client(ADAPTER)
        self.addCleanup(self.client.close)

    def error(self, command, **arguments):
        return self.client.response(self.client.send(command, **arguments), success=False)

    def stop(self):
        c = self.client
        c.request("initialize")
        launch = c.send("launch", program=str(FIXTURE))
        c.event("initialized")
        c.request("setBreakpoints", source={"path": str(SOURCE)}, breakpoints=[{"line": MARKERS["CALL"]}])
        c.request("configurationDone")
        c.response(launch)
        return c.event("stopped")["threadId"]

    def test_requests_before_initialize_are_rejected(self):
        self.error("threads")
        self.client.request("initialize")

    def test_missing_and_malformed_arguments_do_not_crash(self):
        c = self.client
        c.request("initialize")
        for command, args in (
            ("launch", {}),
            ("launch", {"program": None}),
            ("stackTrace", {"threadId": "bad"}),
            ("stackTrace", {"threadId": 1, "startFrame": -1}),
            ("setBreakpoints", {"source": {"path": str(SOURCE)}, "breakpoints": [{"line": 0}]}),
            ("setBreakpoints", {"source": {"path": str(SOURCE)}, "breakpoints": [42]}),
            ("setExceptionBreakpoints", {"filters": None}),
        ):
            with self.subTest(command=command, args=args):
                self.error(command, **args)
        # A valid request still works after every malformed one.
        self.error("evaluate", expression="answer", frameId=1)
        c.request("disconnect")
        self.assertEqual(c.proc.wait(timeout=5), 0)

    def test_duplicate_initialize_and_launch_fail_without_losing_pending_launch(self):
        c = self.client
        c.request("initialize")
        self.error("initialize")
        launch = c.send("launch", program=str(FIXTURE))
        c.event("initialized")
        self.error("launch", program=str(FIXTURE))
        c.request("configurationDone")
        c.response(launch)
        c.event("terminated")

    def test_unsupported_single_thread_request_does_not_resume(self):
        thread = self.stop()
        self.error("continue", threadId=thread, singleThread=True)
        self.assertTrue(self.client.request("stackTrace", threadId=thread)["stackFrames"])

    def test_unsupported_paging_is_not_silently_ignored(self):
        thread = self.stop()
        c = self.client
        frame = c.request("stackTrace", threadId=thread)["stackFrames"][0]
        scope = c.request("scopes", frameId=frame["id"])["scopes"][0]
        self.error("variables", variablesReference=scope["variablesReference"], start=-1, count=1)

    def test_continue_and_exit_events_and_stale_handles(self):
        thread = self.stop()
        c = self.client
        frame = c.request("stackTrace", threadId=thread)["stackFrames"][0]
        scope = c.request("scopes", frameId=frame["id"])["scopes"][0]
        c.request("continue", threadId=thread)
        continued = c.event("continued")
        self.assertEqual(continued["threadId"], thread)
        self.assertTrue(continued["allThreadsContinued"])
        self.assertEqual(c.event("exited")["exitCode"], 0)
        c.event("terminated")
        self.error("scopes", frameId=frame["id"])
        self.error("variables", variablesReference=scope["variablesReference"])
        self.error("launch", program=str(FIXTURE))

    def test_disconnect_completes_pending_launch(self):
        c = self.client
        c.request("initialize")
        launch = c.send("launch", program=str(FIXTURE))
        c.event("initialized")
        c.request("disconnect")
        c.response(launch, success=False)
        self.assertEqual(c.proc.wait(timeout=5), 0)

    def test_failed_launch_cleans_up(self):
        c = self.client
        c.request("initialize")
        launch = c.send("launch", program=str(FIXTURE.with_name("missing.exe")))
        c.event("initialized")
        c.request("configurationDone")
        c.response(launch, success=False)
        self.assertEqual(c.proc.wait(timeout=5), 0)

    def test_eof_terminates_only_the_owned_debuggee(self):
        c = self.client
        c.request("initialize")
        launch = c.send("launch", program=str(FIXTURE), args=["--wait"])
        c.event("initialized")
        c.request("configurationDone")
        c.response(launch)
        pid = c.event("process")["systemProcessId"]
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel.OpenProcess(0x100000, False, pid)
        self.assertTrue(handle)
        try:
            c.close()
            self.assertEqual(kernel.WaitForSingleObject(handle, 2000), 0)
        finally:
            kernel.CloseHandle(handle)

    def test_pause_resume_and_stale_handles(self):
        c = self.client
        c.request("initialize")
        launch = c.send("launch", program=str(FIXTURE), args=["--wait"])
        c.event("initialized")
        c.request("configurationDone")
        c.response(launch)
        c.event("thread")
        for _ in range(3):
            c.request("pause")
            stop = c.event("stopped")
            self.assertEqual(stop["reason"], "pause")
            thread = stop["threadId"]
            frames = c.request("stackTrace", threadId=thread)["stackFrames"]
            self.error("pause")
            c.request("continue", threadId=thread)
            c.event("continued")
            if frames:
                self.error("scopes", frameId=frames[0]["id"])

    def test_malformed_envelope_exits_with_an_explicit_error(self):
        payload = json.dumps({"type": "request", "seq": 1}).encode()
        self.client.proc.stdin.write(f"Content-Length: {len(payload)}\r\n\r\n".encode() + payload)
        self.client.proc.stdin.flush()
        self.assertEqual(self.client.proc.wait(timeout=5), 1)

    def test_opt_in_trace_excludes_paths_arguments_and_variable_values(self):
        c = self.client
        c.request("initialize")
        launch = c.send("launch", program=str(FIXTURE), args=["private-argument"], diagnostics=True)
        c.event("initialized")
        c.request("setBreakpoints", source={"path": str(SOURCE)}, breakpoints=[{"line": MARKERS["CALL"]}])
        c.request("configurationDone")
        c.response(launch)
        stop = c.event("stopped")
        frame = c.request("stackTrace", threadId=stop["threadId"])["stackFrames"][0]
        c.request("evaluate", expression="environment", frameId=frame["id"])
        c.request("continue", threadId=stop["threadId"])
        c.event("terminated")
        traces = [m["body"]["output"] for m in c.messages if m.get("event") == "output" and
                  m["body"]["output"].startswith("[desktopclr]")]
        self.assertTrue(traces)
        text = "".join(traces)
        for secret in (str(FIXTURE), str(SOURCE), "private-argument", "environment", '"Test"'):
            self.assertNotIn(secret, text)
        self.assertIn("request evaluate", text)
        self.assertIn("stopped breakpoint", text)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", type=Path, default=ADAPTER)
    parser.add_argument("--fixture", type=Path, default=FIXTURE)
    args, remaining = parser.parse_known_args()
    ADAPTER, FIXTURE = args.adapter.resolve(), args.fixture.resolve()
    unittest.main(argv=[__file__, *remaining])
