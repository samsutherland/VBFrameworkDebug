import argparse
import json
from pathlib import Path
from queue import Queue
import subprocess
from threading import Thread
import time


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "tests" / "Fixture" / "Program.vb"
MARKERS = {
    line.split("' ")[-1]: i
    for i, line in enumerate(SOURCE.read_text().splitlines(), 1)
    if "' " in line
}


class Client:
    def __init__(self, adapter):
        self.proc = subprocess.Popen(
            [str(adapter)], stdin=subprocess.PIPE, stdout=subprocess.PIPE
        )
        self.queue = Queue()
        self.messages = []
        self.sequence = 0
        Thread(target=self.read, daemon=True).start()

    def read(self):
        stream = self.proc.stdout
        try:
            while first := stream.readline():
                size = int(first.decode().split(":", 1)[1])
                assert stream.readline() == b"\r\n"
                self.queue.put(json.loads(stream.read(size)))
        except Exception as exc:
            self.queue.put(exc)
        finally:
            self.queue.put(EOFError("adapter closed stdout"))

    def send(self, command, **arguments):
        self.sequence += 1
        payload = json.dumps(
            dict(seq=self.sequence, type="request", command=command, arguments=arguments)
        ).encode()
        self.proc.stdin.write(f"Content-Length: {len(payload)}\r\n\r\n".encode() + payload)
        self.proc.stdin.flush()
        return self.sequence

    def expect(self, match):
        deadline = time.monotonic() + 10
        while True:
            for i, msg in enumerate(self.messages):
                if match(msg):
                    return self.messages.pop(i)
            msg = self.queue.get(timeout=max(0, deadline - time.monotonic()))
            if isinstance(msg, Exception):
                raise msg
            self.messages.append(msg)

    def response(self, seq, success=True):
        msg = self.expect(lambda msg: msg.get("request_seq") == seq)
        assert msg["success"] == success, msg
        return msg

    def request(self, command, **arguments):
        return self.response(self.send(command, **arguments)).get("body", {})

    def event(self, name):
        return self.expect(lambda msg: msg.get("event") == name)["body"]

    def close(self):
        self.proc.stdin.close()
        try:
            self.proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait(timeout=5)


def smoke(adapter, fixture, mode):
    client = Client(adapter)
    try:
        client.request("initialize", pathFormat="path", linesStartAt1=True)
        launch = client.send("launch", program=str(fixture), args=["two words"])
        client.event("initialized")
        bp = client.request("setBreakpoints", source={"path": str(SOURCE)},
                            breakpoints=[{"line": MARKERS["CALL"]}])["breakpoints"][0]
        assert not bp["verified"], bp
        client.request("configurationDone")
        response = client.response(launch, success=mode != "wrong-bitness")
        if mode == "wrong-bitness":
            assert "does not match" in response["message"], response
            return
        client.event("process")
        if mode in {"mismatched", "missing"}:
            diagnostic = client.event("output")["output"]
            assert ("does not match" if mode == "mismatched" else "Cannot find symbols") in diagnostic, diagnostic
            client.event("terminated")
            return
        verified = client.event("breakpoint")["breakpoint"]
        assert verified["verified"] and verified["id"] == bp["id"], verified
        stop = client.event("stopped")
        thread = stop["threadId"]
        assert stop["reason"] == "breakpoint", stop
        assert any(t["id"] == thread for t in client.request("threads")["threads"])

        def stack(name, line=None):
            frames = client.request("stackTrace", threadId=thread)["stackFrames"]
            assert frames[0]["name"] == name, frames
            assert Path(frames[0]["source"]["path"]) == SOURCE, frames
            if line is not None:
                assert frames[0]["line"] == line, frames
            return frames

        def locals_at(frame):
            scopes = client.request("scopes", frameId=frame["id"])["scopes"]
            return client.request("variables", variablesReference=scopes[0]["variablesReference"])["variables"]

        main = stack("Program.Main", MARKERS["CALL"])[0]
        locals_by_name = {v["name"]: v for v in locals_at(main)}
        assert locals_by_name["answer"]["value"] == "41", locals_by_name
        assert any(v["name"] == "Count" and v["value"] == "7" for v in client.request(
            "variables", variablesReference=locals_by_name["sample"]["variablesReference"])["variables"])
        assert [v["value"] for v in client.request(
            "variables", variablesReference=locals_by_name["numbers"]["variablesReference"])["variables"]] == ["12", "24"]
        argument_scope = client.request("scopes", frameId=main["id"])["scopes"][1]
        argv = client.request("variables", variablesReference=argument_scope["variablesReference"])["variables"][0]
        words = client.request("variables", variablesReference=argv["variablesReference"])["variables"]
        assert words[0]["value"] == '"two words"', words
        for context in ("repl", "watch", "hover"):
            evaluated = client.request("evaluate", expression=" ENVIRONMENT ", frameId=main["id"], context=context)
            assert evaluated == {"result": '"Test"', "type": "String", "variablesReference": 0}, evaluated
        assert client.request("evaluate", expression="[answer]", frameId=main["id"])["result"] == "41"
        evaluated_args = client.request("evaluate", expression="args", frameId=main["id"])
        assert evaluated_args["variablesReference"] > 0
        assert client.request("variables", variablesReference=evaluated_args["variablesReference"])["variables"][0]["value"] == '"two words"'
        for expression, message in (("absent", "not a local or argument"), ("answer + 1", "Only local"),
                                    ("Increment(answer)", "Only local"), ("", "Only local")):
            response = client.response(client.send("evaluate", expression=expression, frameId=main["id"]), success=False)
            assert message in response["message"], response
        client.response(client.send("evaluate", expression="answer"), success=False)
        if mode == "disconnect":
            client.request("disconnect")
            assert client.proc.wait(timeout=5) == 0
            return
        client.request("stepIn", threadId=thread)
        assert client.event("stopped")["reason"] == "step"
        frames = stack("Program.Increment")
        assert frames[1]["name"] == "Program.Main", frames
        stale = client.send("scopes", frameId=main["id"])
        client.response(stale, success=False)
        client.response(client.send("evaluate", expression="answer", frameId=main["id"]), success=False)
        helper_scopes = client.request("scopes", frameId=frames[0]["id"])["scopes"]
        helper_args = client.request("variables", variablesReference=helper_scopes[1]["variablesReference"])["variables"]
        assert any(v["name"] == "value" for v in helper_args), helper_args
        value = client.request("evaluate", expression="value", frameId=frames[0]["id"])
        assert value["result"] == next(v["value"] for v in helper_args if v["name"] == "value"), value
        assert client.request("evaluate", expression="environment", frameId=frames[1]["id"])["result"] == '"Test"'
        client.request("stepOut", threadId=thread)
        assert client.event("stopped")["reason"] == "step"
        stack("Program.Main")
        client.request("next", threadId=thread)
        assert client.event("stopped")["reason"] == "step"
        stack("Program.Main")
        client.request("continue", threadId=thread)
        client.event("terminated")
        client.request("disconnect")
        assert client.proc.wait(timeout=5) == 0
    except Exception:
        print("Unconsumed messages:", client.messages)
        raise
    finally:
        client.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--adapter", type=Path, default=ROOT / "adapter" / "bin" / "Debug" / "net48" / "DesktopClr.Adapter.exe")
    parser.add_argument("--fixture", type=Path, default=ROOT / "tests" / "Fixture" / "bin" / "Debug" / "net48" / "Fixture.exe")
    parser.add_argument("--mode", default="normal", choices=("normal", "disconnect", "wrong-bitness", "missing", "mismatched"))
    args = parser.parse_args()
    smoke(args.adapter.resolve(), args.fixture.resolve(), args.mode)
    print(f"DAP smoke passed ({args.mode}): {args.fixture}")
