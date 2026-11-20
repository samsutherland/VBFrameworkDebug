import argparse
from pathlib import Path
import unittest

import protocol_test
from smoke import Client


class ExceptionTests(unittest.TestCase):
    def setUp(self):
        self.client = Client(protocol_test.ADAPTER)
        self.addCleanup(self.client.close)
        self.capabilities = self.client.request("initialize")

    def launch(self, mode, filters=None):
        c = self.client
        launch = c.send("launch", program=str(protocol_test.FIXTURE), args=[mode])
        c.event("initialized")
        if filters is not None:
            c.request("setExceptionBreakpoints", filters=filters)
        c.request("configurationDone")
        c.response(launch)

    def test_exception_capabilities_and_filter_validation(self):
        self.assertTrue(self.capabilities["supportsExceptionInfoRequest"])
        self.assertEqual({f["filter"] for f in self.capabilities["exceptionBreakpointFilters"]}, {"thrown", "unhandled"})
        self.client.response(self.client.send("setExceptionBreakpoints", filters=["unknown"]), success=False)

    def test_caught_exception_runs_to_completion_by_default(self):
        self.launch("--caught")
        self.assertEqual(self.client.event("exited")["exitCode"], 0)
        self.client.event("terminated")
        self.assertFalse(any(m.get("event") == "stopped" for m in self.client.messages))

    def test_thrown_exception_details_and_resume(self):
        self.launch("--caught", ["thrown"])
        c = self.client
        stop = c.event("stopped")
        self.assertEqual(stop["reason"], "exception")
        details = c.request("exceptionInfo", threadId=stop["threadId"])
        self.assertEqual(details["exceptionId"], "System.InvalidOperationException")
        self.assertEqual(details["details"]["message"], "fixture failure")
        self.assertEqual(details["breakMode"], "always")
        c.request("continue", threadId=stop["threadId"])
        self.assertEqual(c.event("exited")["exitCode"], 0)
        c.event("terminated")
        self.assertFalse(any(m.get("event") == "stopped" for m in c.messages))

    def test_unhandled_exception_stops_by_default(self):
        self.launch("--unhandled")
        c = self.client
        stop = c.event("stopped")
        details = c.request("exceptionInfo", threadId=stop["threadId"])
        self.assertEqual(details["breakMode"], "unhandled")
        self.assertEqual(details["details"]["message"], "unhandled fixture failure")

    def test_inner_exception_details_are_read_without_getters(self):
        self.launch("--nested")
        stop = self.client.event("stopped")
        info = self.client.request("exceptionInfo", threadId=stop["threadId"])
        self.assertEqual(info["details"]["message"], "outer failure")
        self.assertEqual(info["details"]["innerException"][0]["message"], "inner failure")

    def test_filters_can_be_replaced_during_a_stop(self):
        self.launch("--twice", ["thrown"])
        c = self.client
        stop = c.event("stopped")
        c.request("setExceptionBreakpoints", filters=[])
        c.request("continue", threadId=stop["threadId"])
        c.event("terminated")
        self.assertFalse(any(m.get("event") == "stopped" for m in c.messages))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", type=Path, default=protocol_test.ADAPTER)
    parser.add_argument("--fixture", type=Path, default=protocol_test.FIXTURE)
    args, remaining = parser.parse_known_args()
    protocol_test.ADAPTER, protocol_test.FIXTURE = args.adapter.resolve(), args.fixture.resolve()
    unittest.main(argv=[__file__, *remaining])
