"""Real helper lifecycle checks; sends invalid requests only, never discovers hardware."""
import argparse
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading


def start_helper(host, parent):
    return subprocess.Popen([str(host), "--parent-pid", str(parent)], stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)


def invalid_request(helper):
    results = queue.Queue()
    threading.Thread(target=lambda: results.put(helper.stdout.readline()), daemon=True).start()
    helper.stdin.write(b'{"version":1,"requestId":731,"operation":"invalid"}\n')
    helper.stdin.flush()
    try:
        raw = results.get(timeout=5)
    except queue.Empty:
        raise AssertionError("helper_response_timeout")
    if not raw:
        raise AssertionError("helper_closed_early:" + helper.stderr.read(512).decode("utf-8", errors="replace"))
    reply = json.loads(raw)
    assert reply["version"] == 1 and reply["requestId"] == 731 and reply["error"] == "unsupported_operation", reply
    assert reply["devices"] == [] and reply["claimedPaths"] == [], reply


def cleanup(process):
    if process.poll() is None:
        process.kill()
        process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    host = parser.parse_args().host.resolve(strict=True)
    helper = start_helper(host, os.getpid())
    try:
        invalid_request(helper)
        helper.stdin.close()
        assert helper.wait(timeout=3) == 0, "stdin_eof_exit_code"
        assert helper.stderr.read() == b"", "stdin_eof_unexpected_stderr"
    finally:
        cleanup(helper)
    parent = subprocess.Popen([sys.executable, "-c", "import sys; sys.stdin.buffer.read()"],
                              stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                              creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    helper = start_helper(host, parent.pid)
    try:
        invalid_request(helper)
        parent.stdin.close()
        assert parent.wait(timeout=3) == 0, "fixture_parent_exit"
        assert helper.wait(timeout=3) == 0, "parent_exit_did_not_stop_helper"
        assert helper.stderr.read() == b"", "parent_exit_unexpected_stderr"
    finally:
        cleanup(helper)
        cleanup(parent)
    print("PASS correlated invalid-operation response; stdin EOF exit; parent-exit termination; no hardware access")


if __name__ == "__main__":
    main()
