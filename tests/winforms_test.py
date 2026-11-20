import argparse
from pathlib import Path
import unittest

import protocol_test
from smoke import Client, ROOT


SOURCE = ROOT / "tests" / "WinFormsFixture" / "Program.vb"
MARKERS = {line.split("' ")[-1]: i for i, line in enumerate(SOURCE.read_text().splitlines(), 1) if "' " in line}
FIXTURE = ROOT / "dist" / "fixtures" / "winforms-x64-portable" / "WinFormsFixture.exe"


class WinFormsTests(unittest.TestCase):
    def launch(self, markers, filters):
        c = Client(protocol_test.ADAPTER)
        self.addCleanup(c.close)
        c.request("initialize")
        launch = c.send("launch", program=str(FIXTURE))
        c.event("initialized")
        c.request("setBreakpoints", source={"path": str(SOURCE)}, breakpoints=[{"line": MARKERS[m]} for m in markers])
        c.request("setExceptionBreakpoints", filters=filters)
        c.request("configurationDone")
        c.response(launch)
        return c

    def test_constructor_worker_and_awaited_event_handler(self):
        c = self.launch(["FORMCTOR", "BEFOREAWAIT", "WORKER", "AFTERAWAIT"], [])
        ui_thread = None
        for marker in ("FORMCTOR", "BEFOREAWAIT", "WORKER", "AFTERAWAIT"):
            stop = c.event("stopped")
            thread = stop["threadId"]
            frames = c.request("stackTrace", threadId=thread)["stackFrames"]
            frame = frames[0]
            self.assertEqual(frame["line"], MARKERS[marker], frames)
            if marker == "FORMCTOR":
                ui_thread = thread
                self.assertEqual(c.request("evaluate", expression="environment", frameId=frame["id"])["result"], '"Test"')
            elif marker == "WORKER":
                self.assertNotEqual(thread, ui_thread)
            else:
                self.assertEqual(thread, ui_thread)
                self.assertEqual(c.request("evaluate", expression="Me.Environment", frameId=frame["id"])["result"], '"Test"')
                self.assertEqual(c.request("evaluate", expression="saved", frameId=frame["id"])["result"], '"Test"')
            c.request("continue", threadId=thread)
            c.response(c.send("scopes", frameId=frame["id"]), success=False)
        c.event("terminated")

    def test_framework_caught_exception_requires_thrown_filter(self):
        c = self.launch(["FORMTHROW"], ["thrown"])
        stop = c.event("stopped")
        c.request("continue", threadId=stop["threadId"])
        stop = c.event("stopped")
        self.assertEqual(stop["reason"], "exception")
        info = c.request("exceptionInfo", threadId=stop["threadId"])
        self.assertEqual(info["details"]["message"], "WinForms caught exception")
        c.request("setExceptionBreakpoints", filters=[])
        c.request("continue", threadId=stop["threadId"])
        c.event("terminated")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", type=Path, default=protocol_test.ADAPTER)
    parser.add_argument("--fixture", type=Path, default=FIXTURE)
    args, remaining = parser.parse_known_args()
    protocol_test.ADAPTER, FIXTURE = args.adapter.resolve(), args.fixture.resolve()
    unittest.main(argv=[__file__, *remaining])
