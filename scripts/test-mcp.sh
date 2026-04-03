#!/bin/bash
# Smoke-test the unifocl MCP server via stdio JSON-RPC.
# Usage: ./scripts/test-mcp.sh [path-to-binary]
set -euo pipefail

BINARY="${1:-src/unifocl/bin/Debug/net10.0/unifocl}"
FIFO=$(mktemp -u /tmp/mcp-fifo.XXXXXX)
mkfifo "$FIFO"
STDOUT_LOG=$(mktemp /tmp/mcp-stdout.XXXXXX)
STDERR_LOG=$(mktemp /tmp/mcp-stderr.XXXXXX)

cleanup() { rm -f "$FIFO" "$STDOUT_LOG" "$STDERR_LOG"; }
trap cleanup EXIT

# Launch MCP server with fifo as stdin
"$BINARY" mcp-server < "$FIFO" > "$STDOUT_LOG" 2> "$STDERR_LOG" &
MCP_PID=$!

# Open fifo for writing (keeps server alive)
exec 3>"$FIFO"

send() {
    echo "$1" >&3
}

wait_response() {
    local id="$1"
    local label="$2"
    local max_wait=5
    local elapsed=0
    while [ $elapsed -lt $max_wait ]; do
        if grep -q "\"id\":${id}" "$STDOUT_LOG" 2>/dev/null; then
            # Extract the response line for this id
            local line
            line=$(grep "\"id\":${id}" "$STDOUT_LOG" | head -1)
            if echo "$line" | python3 -m json.tool > /dev/null 2>&1; then
                echo "PASS: $label"
                echo "$line" | python3 -m json.tool 2>/dev/null | head -30
                echo ""
                return 0
            fi
        fi
        sleep 0.2
        elapsed=$((elapsed + 1))
    done
    echo "FAIL: $label (no response within ${max_wait}s)"
    echo "  stdout so far: $(cat "$STDOUT_LOG")"
    echo "  stderr tail:   $(tail -5 "$STDERR_LOG")"
    echo ""
    return 1
}

PASS=0
FAIL=0

run_test() {
    if wait_response "$1" "$2"; then
        PASS=$((PASS + 1))
    else
        FAIL=$((FAIL + 1))
    fi
}

echo "=== unifocl MCP smoke tests ==="
echo "Binary: $BINARY"
echo ""

# 1. Initialize
send '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}'
run_test 1 "initialize"

# 2. Notify initialized
send '{"jsonrpc":"2.0","method":"notifications/initialized"}'
sleep 0.3

# 3. tools/list
send '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
run_test 2 "tools/list"

# 4. list_commands (core category)
send '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_commands","arguments":{"scope":"root","category":"core"}}}'
run_test 3 "list_commands(core)"

# 5. list_commands (all categories)
send '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_commands","arguments":{"scope":"root","category":"all","limit":5}}}'
run_test 4 "list_commands(all, limit=5)"

# 6. list_commands (build category)
send '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"list_commands","arguments":{"scope":"root","category":"build"}}}'
run_test 5 "list_commands(build)"

# 7. lookup_command
send '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"lookup_command","arguments":{"command":"/open"}}}'
run_test 6 "lookup_command(/open)"

# 8. get_agent_workflow_guide (quick_start)
send '{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"get_agent_workflow_guide","arguments":{"section":"quick_start"}}}'
run_test 7 "get_agent_workflow_guide(quick_start)"

# 9. get_agent_workflow_guide (exec_flags)
send '{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"get_agent_workflow_guide","arguments":{"section":"exec_flags"}}}'
run_test 8 "get_agent_workflow_guide(exec_flags)"

# 10. get_agent_workflow_guide (invalid section)
send '{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"get_agent_workflow_guide","arguments":{"section":"nonexistent"}}}'
run_test 9 "get_agent_workflow_guide(invalid → error+available)"

# 11. get_mutate_schema
send '{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"get_mutate_schema","arguments":{}}}'
run_test 10 "get_mutate_schema"

# 12. validate_mutate_batch (valid)
send '{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"validate_mutate_batch","arguments":{"opsJson":"[{\"op\":\"create\",\"type\":\"canvas\",\"name\":\"HUD\"}]"}}}'
run_test 11 "validate_mutate_batch(valid)"

# 13. validate_mutate_batch (invalid)
send '{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"validate_mutate_batch","arguments":{"opsJson":"[{\"op\":\"create\"}]"}}}'
run_test 12 "validate_mutate_batch(missing type)"

# 14. get_categories (no project open — should return empty or hint)
send '{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"get_categories","arguments":{}}}'
run_test 13 "get_categories"

# 15. use_category (no project — should return error)
send '{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"use_category","arguments":{"categoryName":"profiling"}}}'
run_test 14 "use_category(no project → error)"

# Shutdown
exec 3>&-
wait $MCP_PID 2>/dev/null || true

echo "=== Results: $PASS passed, $FAIL failed ==="
exit $FAIL
