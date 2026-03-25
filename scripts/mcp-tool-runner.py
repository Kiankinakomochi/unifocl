#!/usr/bin/env python3
"""
scripts/mcp-tool-runner.py — MCP tool test runner for the unifocl test suite.

Invoked by run-testcases.sh as a drop-in runner for MCP-layer tests.
Receives the standard runner-appended arguments:

    exec "<tool_name> [args_json]" --agentic --format json \
        --session-seed <seed> --project <path> --mode <mode>

Starts the unifocl MCP server via stdio (newline-delimited JSON-RPC),
performs the initialize handshake, calls the requested tool, reads the
response, and prints an AgenticResponseEnvelope-compatible JSON object
to stdout.

Exit code: 0 always (errors are encoded in the envelope).
"""

import json
import os
import subprocess
import sys
import time


def parse_args(argv):
    """Return (tool_name, tool_args_dict, project_path) from the runner argv."""
    args = argv[1:]
    cmd_text = ""
    project_path = ""

    i = 0
    while i < len(args):
        if args[i] == "exec" and i + 1 < len(args):
            cmd_text = args[i + 1]
            i += 2
        elif args[i] == "--project" and i + 1 < len(args):
            project_path = args[i + 1]
            i += 2
        else:
            i += 1

    parts = cmd_text.strip().split(None, 1)
    tool_name = parts[0] if parts else ""
    tool_args = {}
    if len(parts) > 1:
        try:
            tool_args = json.loads(parts[1])
        except json.JSONDecodeError:
            pass

    return tool_name, tool_args, project_path


def error_envelope(message):
    return json.dumps({
        "status": "error",
        "meta": {"exitCode": 1},
        "data": None,
        "errors": [message]
    })


def send_line(proc, obj):
    proc.stdin.write((json.dumps(obj) + "\n").encode())
    proc.stdin.flush()


def recv_line(proc, target_id, timeout=10):
    """Read newline-delimited JSON until a message with the given id is found."""
    buf = b""
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            chunk = os.read(proc.stdout.fileno(), 4096)
            if chunk:
                buf += chunk
        except BlockingIOError:
            time.sleep(0.05)
            continue

        while b"\n" in buf:
            line, buf = buf.split(b"\n", 1)
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
                if msg.get("id") == target_id:
                    return msg
            except json.JSONDecodeError:
                continue

    return None


def main():
    tool_name, tool_args, project_path = parse_args(sys.argv)

    if not tool_name:
        print(error_envelope("could not parse tool_name from args"))
        return

    script_dir = os.path.dirname(os.path.abspath(__file__))
    unifocl_csproj = os.path.join(script_dir, "..", "src", "unifocl", "unifocl.csproj")

    env = dict(os.environ)
    if project_path:
        env["UNIFOCL_UNITY_PROJECT_PATH"] = project_path

    try:
        proc = subprocess.Popen(
            ["dotnet", "run", "--project", unifocl_csproj, "--no-build", "--", "mcp-server"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            env=env,
        )
    except Exception as ex:
        print(error_envelope(f"failed to start mcp-server: {ex}"))
        return

    try:
        # Wait for dotnet startup
        time.sleep(3)

        # 1. Initialize
        send_line(proc, {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "test-runner", "version": "1.0"}
            }
        })
        init_resp = recv_line(proc, 1, timeout=10)
        if init_resp is None:
            print(error_envelope("timed out waiting for initialize response"))
            return

        # 2. Notify initialized
        send_line(proc, {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})
        time.sleep(0.2)

        # 3. Call the tool
        send_line(proc, {
            "jsonrpc": "2.0",
            "id": 2,
            "method": "tools/call",
            "params": {"name": tool_name, "arguments": tool_args}
        })
        call_resp = recv_line(proc, 2, timeout=15)
        if call_resp is None:
            print(error_envelope(f"timed out waiting for tools/call response for '{tool_name}'"))
            return

        if "error" in call_resp:
            err_msg = call_resp["error"].get("message", "unknown rpc error")
            print(error_envelope(f"tools/call error: {err_msg}"))
            return

        content = call_resp.get("result", {}).get("content", [])
        text = content[0].get("text", "{}") if content else "{}"

        try:
            data = json.loads(text)
        except json.JSONDecodeError:
            data = {"text": text}

        print(json.dumps({
            "status": "ok",
            "meta": {"exitCode": 0},
            "data": data,
            "errors": []
        }))

    finally:
        try:
            proc.kill()
            proc.wait(timeout=3)
        except Exception:
            pass


if __name__ == "__main__":
    main()
