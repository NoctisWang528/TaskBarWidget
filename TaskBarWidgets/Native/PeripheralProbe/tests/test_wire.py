"""Offline pipe/lifetime checks. Never sends discover or a discovered hardware identity."""
import argparse
import json
import os
import queue
import subprocess
import threading


def start(executable):
    process = subprocess.Popen(
        [executable, "--parent-pid", str(os.getpid())],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    lines = queue.Queue()

    def receive():
        for line in process.stdout:
            lines.put(line)
        lines.put(None)

    threading.Thread(target=receive, daemon=True).start()
    return process, lines


def exchange(process, lines, value):
    line = value if isinstance(value, bytes) else json.dumps(value).encode("utf-8")
    process.stdin.write(line + b"\n")
    process.stdin.flush()
    result = lines.get(timeout=5)
    assert result is not None, "worker exited before replying"
    assert len(result) <= 1024 * 1024
    return json.loads(result)


def main(executable):
    subprocess.run([executable, "--self-test"], check=True, timeout=10)
    process, lines = start(executable)
    try:
        request = {"version": 1, "requestId": "unicode-设备", "operation": "read",
                   "deviceIds": ["synthetic-untrusted-path"], "vendorExtensionsEnabled": False}
        reply = exchange(process, lines, request)
        assert reply["requestId"] == request["requestId"] and reply["version"] == 1
        assert reply["error"] == "" and reply["initialized"] == []
        device = reply["devices"][0]
        assert device["identity"] == "synthetic-untrusted-path"
        assert device["state"] == "unsupported" and not device["canRead"]
        assert device["batteries"] == [] and device["updatedAt"] is None
        request["excludedIdentities"] = request["deviceIds"]
        assert exchange(process, lines, request)["devices"] == []
        for malformed in [b"{", b'{"version":1,"version":1,"requestId":7,"operation":"read"}',
                          b'{"version":2,"requestId":7,"operation":"read"}',
                          b'{"version":1,"requestId":7,"operation":"read","deviceIds":[7]}',
                          b'{"version":1,"requestId":7,"operation":"read","vendorExtensionsEnabled":1}',
                          b'{"version":1,"requestId":7,"operation":"read","excludedIdentities":"bad"}',
                          b'{"version":1,"requestId":7,"operation":"read","attemptedInitializations":[null]}']:
            assert exchange(process, lines, malformed)["error"] == "invalid-request"
        request = {"version": 1, "requestId": 9223372036854775807, "operation": "read", "deviceIds": []}
        assert exchange(process, lines, request)["requestId"] == request["requestId"]
        request["operation"] = "identify"
        request["deviceIds"] = ["synthetic-untrusted-path"]
        assert exchange(process, lines, request)["error"] == "invalid-identify-target"
        assert process.poll() is None, "worker should stay alive between requests"
        process.stdin.close()
        assert process.wait(timeout=5) == 0, "EOF must end worker"
    finally:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=5)
    # Oversized lines fail closed without allocating unbounded buffers or touching hardware.
    process, _ = start(executable)
    try:
        try:
            process.stdin.write(b"x" * (1024 * 1024 + 1))
            process.stdin.flush()
        except (BrokenPipeError, OSError):
            pass
        assert process.wait(timeout=5) == 4
    finally:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=5)
    # Invalid parent and missing parent argument cannot enter hardware mode.
    for arguments in [[], ["--parent-pid", "0"]]:
        assert subprocess.run([executable, *arguments], capture_output=True, timeout=5).returncode == 2
    print("Peripheral probe offline wire/lifetime checks passed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("executable")
    main(parser.parse_args().executable)
