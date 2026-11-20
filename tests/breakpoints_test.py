import argparse
from pathlib import Path
import unittest

import protocol_test
from smoke import Client, SOURCE, MARKERS


class BreakpointTests(unittest.TestCase):
    def setUp(self):
        self.client = Client(protocol_test.ADAPTER)
        self.addCleanup(self.client.close)
        self.client.request("initialize")

    def launch(self, spec, mode="--count", marker="LOOP"):
        c = self.client
        launch = c.send("launch", program=str(protocol_test.FIXTURE), args=[mode])
        c.event("initialized")
        c.request("setBreakpoints", source={"path": str(SOURCE)}, breakpoints=[{"line": MARKERS[marker], **spec}])
        c.request("configurationDone")
        c.response(launch)

    def test_condition_and_hit_count_stop_on_exact_iteration(self):
        self.launch({"condition": "iteration > 2", "hitCondition": "== 4"})
        c = self.client
        stop = c.event("stopped")
        frame = c.request("stackTrace", threadId=stop["threadId"])["stackFrames"][0]
        self.assertEqual(c.request("evaluate", expression="iteration", frameId=frame["id"])["result"], "4")
        c.request("continue", threadId=stop["threadId"])
        c.event("terminated")
        self.assertFalse(any(m.get("event") == "stopped" for m in c.messages))

    def test_logpoint_outputs_without_stopping(self):
        self.launch({"logMessage": "count={iteration} {{ok}}", "hitCondition": "% 2"})
        c = self.client
        c.event("terminated")
        output = [m["body"]["output"] for m in c.messages if m.get("event") == "output"]
        self.assertEqual(output, ["count=2 {ok}\n", "count=4 {ok}\n"])
        self.assertFalse(any(m.get("event") == "stopped" for m in c.messages))

    def test_invalid_breakpoint_syntax_rejected_before_launch(self):
        for spec in ({"condition": "1 +"}, {"hitCondition": "% 0"}, {"hitCondition": "nonsense"},
                     {"logMessage": "{unterminated"}):
            with self.subTest(spec=spec):
                self.client.response(self.client.send("setBreakpoints", source={"path": str(SOURCE)},
                    breakpoints=[{"line": MARKERS["LOOP"], **spec}]), success=False)

    def test_condition_failure_stops_instead_of_skipping(self):
        self.launch({"condition": "missing = 1"})
        c = self.client
        c.event("stopped")
        self.assertIn("condition", c.event("output")["output"].lower())

    def test_replacing_breakpoint_resets_its_counter(self):
        self.launch({"hitCondition": "== 2"})
        c = self.client
        stop = c.event("stopped")
        c.request("setBreakpoints", source={"path": str(SOURCE)},
                  breakpoints=[{"line": MARKERS["LOOP"], "hitCondition": "== 3"}])
        c.request("continue", threadId=stop["threadId"])
        stop = c.event("stopped")
        frame = c.request("stackTrace", threadId=stop["threadId"])["stackFrames"][0]
        self.assertEqual(c.request("evaluate", expression="iteration", frameId=frame["id"])["result"], "5")

    def test_breakpoints_survive_appdomain_unload_and_reload(self):
        self.launch({"hitCondition": "== 6"}, "--domain")
        c = self.client
        stop = c.event("stopped")
        frame = c.request("stackTrace", threadId=stop["threadId"])["stackFrames"][0]
        self.assertEqual(c.request("evaluate", expression="iteration", frameId=frame["id"])["result"], "1")
        self.assertTrue(any(m.get("event") == "module" and m["body"]["reason"] == "removed" for m in c.messages))
        c.request("continue", threadId=stop["threadId"])
        c.event("terminated")

    def test_all_executable_statements_on_one_line_bind(self):
        self.launch({}, "--multi", "MULTI")
        for _ in range(2):
            stop = self.client.event("stopped")
            frame = self.client.request("stackTrace", threadId=stop["threadId"])["stackFrames"][0]
            self.assertEqual(frame["line"], MARKERS["MULTI"])
            self.client.request("continue", threadId=stop["threadId"])
        self.client.event("terminated")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", type=Path, default=protocol_test.ADAPTER)
    parser.add_argument("--fixture", type=Path, default=protocol_test.FIXTURE)
    args, remaining = parser.parse_known_args()
    protocol_test.ADAPTER, protocol_test.FIXTURE = args.adapter.resolve(), args.fixture.resolve()
    unittest.main(argv=[__file__, *remaining])
