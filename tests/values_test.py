import argparse
from pathlib import Path
import unittest

import protocol_test
from smoke import Client, ROOT, SOURCE, MARKERS


class ValueTests(unittest.TestCase):
    def setUp(self):
        self.client = Client(protocol_test.ADAPTER)
        self.addCleanup(self.client.close)
        c = self.client
        c.request("initialize")
        launch = c.send("launch", program=str(protocol_test.FIXTURE), args=["one"])
        c.event("initialized")
        c.request("setBreakpoints", source={"path": str(SOURCE)}, breakpoints=[{"line": MARKERS["CALL"]}])
        c.request("configurationDone")
        c.response(launch)
        thread = c.event("stopped")["threadId"]
        self.frame = c.request("stackTrace", threadId=thread)["stackFrames"][0]["id"]

    def evaluate(self, expression):
        return self.client.request("evaluate", expression=expression, frameId=self.frame)

    def test_field_chains_and_array_indices(self):
        for expression, expected in (
            ("sample.Count", "7"), ("SAMPLE.[Count]", "7"),
            ("sample.BaseCount", "9"), ("(sample).Count", "7"),
            ("numbers(1)", "24"), ("args(0)", '"one"'),
            ("matrix(-1, 3)", "99"), ("boxed", "123"),
            ("price", "12.5"), ("status", "Friday (5)"),
        ):
            with self.subTest(expression=expression):
                self.assertEqual(self.evaluate(expression)["result"], expected)

    def test_nothing_getters_and_invalid_indices_fail_explicitly(self):
        for expression in ("sample.Dangerous", "numbers(5)", "numbers(-1)", "sample.Unknown",
                           "numbers(0, 0)", "missing.Count", "sample.ToString()", "empty.Count"):
            with self.subTest(expression=expression):
                result = self.client.response(self.client.send(
                    "evaluate", expression=expression, frameId=self.frame), success=False)
                self.assertTrue(result["message"])
        self.assertEqual(self.evaluate("sample.GetterCalls")["result"], "0")

    def test_array_paging_uses_actual_dimensions_and_bounds(self):
        value = self.evaluate("matrix")
        self.assertEqual(value["indexedVariables"], 6)
        ref = value["variablesReference"]
        first = self.client.request("variables", variablesReference=ref, filter="indexed", start=0, count=2)["variables"]
        second = self.client.request("variables", variablesReference=ref, filter="indexed", start=2, count=2)["variables"]
        self.assertEqual([v["name"] for v in first], ["(-1, 2)", "(-1, 3)"])
        self.assertEqual([v["name"] for v in second], ["(-1, 4)", "(0, 2)"])
        self.assertEqual(first[1]["value"], "99")
        self.assertEqual(self.client.request("variables", variablesReference=ref, start=6, count=1)["variables"], [])

    def test_scopes_and_children_have_evaluate_names(self):
        scopes = self.client.request("scopes", frameId=self.frame)["scopes"]
        locals_ = self.client.request("variables", variablesReference=scopes[0]["variablesReference"])["variables"]
        self.assertEqual(scopes[0]["namedVariables"], len(locals_))
        sample = next(v for v in locals_ if v["name"] == "sample")
        self.assertEqual(sample["evaluateName"], "[sample]")
        fields = self.client.request("variables", variablesReference=sample["variablesReference"])["variables"]
        count = next(v for v in fields if v["name"] == "Count")
        self.assertEqual(self.evaluate(count["evaluateName"])["result"], "7")
        matrix = self.evaluate("matrix")
        element = self.client.request("variables", variablesReference=matrix["variablesReference"], start=1, count=1)["variables"][0]
        self.assertEqual(self.evaluate(element["evaluateName"])["result"], "99")

class ConstructorTests(unittest.TestCase):
    def test_constructor_arguments_and_me_fields(self):
        c = Client(protocol_test.ADAPTER)
        self.addCleanup(c.close)
        c.request("initialize")
        launch = c.send("launch", program=str(protocol_test.FIXTURE))
        c.event("initialized")
        c.request("setBreakpoints", source={"path": str(SOURCE)}, breakpoints=[{"line": MARKERS["CTOR"]}])
        c.request("configurationDone")
        c.response(launch)
        thread = c.event("stopped")["threadId"]
        frame = c.request("stackTrace", threadId=thread)["stackFrames"][0]["id"]
        self.assertEqual(c.request("evaluate", expression="environment", frameId=frame)["result"], '"Test"')
        self.assertEqual(c.request("evaluate", expression="Me.BaseCount", frameId=frame)["result"], "9")
        self.assertGreater(c.request("evaluate", expression="Me", frameId=frame)["variablesReference"], 0)

    def test_byref_argument_reads_the_referenced_value(self):
        c = Client(protocol_test.ADAPTER)
        self.addCleanup(c.close)
        c.request("initialize")
        launch = c.send("launch", program=str(protocol_test.FIXTURE))
        c.event("initialized")
        c.request("setBreakpoints", source={"path": str(SOURCE)}, breakpoints=[{"line": MARKERS["BYREF"]}])
        c.request("configurationDone")
        c.response(launch)
        thread = c.event("stopped")["threadId"]
        frame = c.request("stackTrace", threadId=thread)["stackFrames"][0]["id"]
        self.assertEqual(c.request("evaluate", expression="number", frameId=frame)["result"], "9")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", type=Path, default=protocol_test.ADAPTER)
    parser.add_argument("--fixture", type=Path, default=protocol_test.FIXTURE)
    args, remaining = parser.parse_known_args()
    protocol_test.ADAPTER, protocol_test.FIXTURE = args.adapter.resolve(), args.fixture.resolve()
    unittest.main(argv=[__file__, *remaining])
