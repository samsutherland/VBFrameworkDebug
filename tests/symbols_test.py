import argparse
from pathlib import Path
import tempfile
import unittest

import protocol_test
from smoke import Client, SOURCE, MARKERS


class SymbolTests(unittest.TestCase):
    def launch(self, source, mapping, line=None):
        c = Client(protocol_test.ADAPTER)
        self.addCleanup(c.close)
        caps = c.request("initialize")
        self.assertTrue(caps["supportsModulesRequest"])
        launch = c.send("launch", program=str(protocol_test.FIXTURE), sourceFileMap=mapping)
        c.event("initialized")
        c.request("setBreakpoints", source={"path": str(source)}, breakpoints=[{"line": line or MARKERS["CALL"]}])
        c.request("configurationDone")
        c.response(launch)
        return c

    def test_module_status_and_mapped_source_breakpoint(self):
        with tempfile.TemporaryDirectory() as directory:
            copy = Path(directory) / SOURCE.name
            copy.write_bytes(SOURCE.read_bytes())
            c = self.launch(copy, {str(SOURCE.parent): directory})
            stopped = c.event("stopped")
            top = c.request("stackTrace", threadId=stopped["threadId"])["stackFrames"][0]
            self.assertTrue(Path(top["source"]["path"]).samefile(copy))
            modules = c.request("modules", startModule=0, moduleCount=0)
            self.assertGreater(modules["totalModules"], 1)
            exe = next(m for m in modules["modules"] if m["path"] == str(protocol_test.FIXTURE))
            self.assertEqual(exe["symbolStatus"], "Symbols loaded")
            self.assertEqual(exe["architecture"], protocol_test.ADAPTER.parent.name)
            c.request("disconnect")
            c.proc.wait(timeout=5)

    def test_changed_source_is_not_silently_bound(self):
        with tempfile.TemporaryDirectory() as directory:
            copy = Path(directory) / SOURCE.name
            copy.write_bytes(SOURCE.read_bytes() + b"\r\n' changed\r\n")
            c = self.launch(copy, {str(SOURCE.parent): directory})
            c.event("terminated")
            changes = [m["body"]["breakpoint"] for m in c.messages if m.get("event") == "breakpoint"]
            self.assertTrue(changes)
            self.assertFalse(changes[-1]["verified"])
            self.assertIn("checksum", changes[-1]["message"].lower())

    def test_comment_relocation_stays_within_the_containing_method(self):
        c = self.launch(SOURCE, {}, MARKERS["BLANK"])
        stop = c.event("stopped")
        frame = c.request("stackTrace", threadId=stop["threadId"])["stackFrames"][0]
        self.assertEqual(frame["line"], MARKERS["BLANK"] + 1)
        locations = c.request("breakpointLocations", source={"path": str(SOURCE)},
                              line=MARKERS["BLANK"], endLine=MARKERS["BLANK"] + 1)["breakpoints"]
        self.assertEqual({p["line"] for p in locations}, {MARKERS["BLANK"] + 1})


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", type=Path, default=protocol_test.ADAPTER)
    parser.add_argument("--fixture", type=Path, default=protocol_test.FIXTURE)
    args, remaining = parser.parse_known_args()
    protocol_test.ADAPTER, protocol_test.FIXTURE = args.adapter.resolve(), args.fixture.resolve()
    unittest.main(argv=[__file__, *remaining])
