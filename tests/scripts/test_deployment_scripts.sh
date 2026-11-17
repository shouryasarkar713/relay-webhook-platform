#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Automated Acceptance & Failure Injection Tests for Deployment & Smoke Scripts
# ==============================================================================

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${REPO_ROOT}"

PASS_COUNT=0
FAIL_COUNT=0

assert_test() {
    local name="$1"
    local result="$2"
    if [ "$result" -eq 0 ]; then
        echo "  [PASS] ${name}"
        PASS_COUNT=$((PASS_COUNT + 1))
    else
        echo "  [FAIL] ${name}"
        FAIL_COUNT=$((FAIL_COUNT + 1))
    fi
}

TMP_TEST_DIR=$(mktemp -d)
trap 'rm -rf "${TMP_TEST_DIR}"' EXIT

echo "=============================================================================="
echo "RUNNING SCRIPT BEHAVIOUR & SAFETY INVARIANT TESTS"
echo "=============================================================================="

# ------------------------------------------------------------------------------
# Test 1: Smoke test fails when RELAY_SMOKE_API_KEY is missing
# ------------------------------------------------------------------------------
OUT_1=$(RELAY_SMOKE_API_KEY="" ./deploy/scripts/smoke-test.sh 2>&1 || true)
if echo "${OUT_1}" | grep -q "RELAY_SMOKE_API_KEY environment variable is required"; then
    assert_test "Smoke test aborts cleanly when RELAY_SMOKE_API_KEY is missing" 0
else
    assert_test "Smoke test aborts cleanly when RELAY_SMOKE_API_KEY is missing" 1
fi

# ------------------------------------------------------------------------------
# Test 2: Dry-run performs zero cloud mutation and exits 0
# ------------------------------------------------------------------------------
OUT_2=$(./deploy/scripts/deploy.sh --dry-run 2>&1 || true)
if echo "${OUT_2}" | grep -q "\[DRY-RUN COMPLETE\]"; then
    assert_test "deploy.sh --dry-run succeeds locally without cloud mutation" 0
else
    assert_test "deploy.sh --dry-run succeeds locally without cloud mutation" 1
fi

# ------------------------------------------------------------------------------
# Test 3: Subscription guard stops mismatching subscription
# ------------------------------------------------------------------------------
MOCK_BIN="${TMP_TEST_DIR}/bin"
mkdir -p "${MOCK_BIN}"

cat << 'EOF' > "${MOCK_BIN}/az"
#!/usr/bin/env bash
if [ "$1" = "account" ] && [ "$2" = "show" ]; then
    echo '{"id":"actual-sub-1111","name":"Test Sub","state":"Enabled","user":{"name":"dev@example.com"}}'
    exit 0
fi
EOF
chmod +x "${MOCK_BIN}/az"

OUT_3=$(PATH="${MOCK_BIN}:$PATH" ./deploy/scripts/deploy.sh --subscription=expected-sub-9999 --preflight 2>&1 || true)
if echo "${OUT_3}" | grep -q "Target subscription ID does not match active Azure CLI subscription"; then
    assert_test "deploy.sh blocks mismatched subscription ID" 0
else
    assert_test "deploy.sh blocks mismatched subscription ID" 1
fi

# ------------------------------------------------------------------------------
# Test 4: Preflight detects un-registered resource provider and exits nonzero
# ------------------------------------------------------------------------------
cat << 'EOF' > "${MOCK_BIN}/az"
#!/usr/bin/env bash
if [ "$1" = "account" ] && [ "$2" = "show" ]; then
    echo '{"id":"sub-1111","name":"Test Sub","state":"Enabled","user":{"name":"dev@example.com"}}'
    exit 0
elif [ "$1" = "ad" ]; then
    echo "00000000-0000-0000-0000-000000000000"
    exit 0
elif [ "$1" = "provider" ] && [ "$2" = "show" ]; then
    if [ "$4" = "Microsoft.Sql" ]; then
        echo "NotRegistered"
    else
        echo "Registered"
    fi
    exit 0
fi
EOF
chmod +x "${MOCK_BIN}/az"

OUT_4=$(PATH="${MOCK_BIN}:$PATH" ./deploy/scripts/deploy.sh --preflight 2>&1 || true)
if echo "${OUT_4}" | grep -q "\[PREFLIGHT FAILED\]"; then
    assert_test "Preflight fails nonzero when a required provider is NotRegistered" 0
else
    assert_test "Preflight fails nonzero when a required provider is NotRegistered" 1
fi

# ------------------------------------------------------------------------------
# Test 5: Operator declining preliminary RG creation aborts safely
# ------------------------------------------------------------------------------
cat << 'EOF' > "${MOCK_BIN}/az"
#!/usr/bin/env bash
if [ "$1" = "account" ] && [ "$2" = "show" ]; then
    echo '{"id":"sub-1111","name":"Test Sub","state":"Enabled","user":{"name":"dev@example.com"}}'
    exit 0
elif [ "$1" = "ad" ]; then
    echo "00000000-0000-0000-0000-000000000000"
    exit 0
elif [ "$1" = "group" ] && [ "$2" = "exists" ]; then
    echo "false"
    exit 0
fi
EOF
chmod +x "${MOCK_BIN}/az"

OUT_5=$(echo "no" | PATH="${MOCK_BIN}:$PATH" ./deploy/scripts/deploy.sh 2>&1 || true)
if echo "${OUT_5}" | grep -q "Deployment aborted by operator. No resources were created or modified."; then
    assert_test "Declining Stage 2 RG creation aborts cleanly without provisioning" 0
else
    assert_test "Declining Stage 2 RG creation aborts cleanly without provisioning" 1
fi

# ------------------------------------------------------------------------------
# Test 6: Bicep What-If failure prevents deployment approval
# ------------------------------------------------------------------------------
cat << 'EOF' > "${MOCK_BIN}/az"
#!/usr/bin/env bash
if [ "$1" = "account" ] && [ "$2" = "show" ]; then
    echo '{"id":"sub-1111","name":"Test Sub","state":"Enabled","user":{"name":"dev@example.com"}}'
    exit 0
elif [ "$1" = "ad" ]; then
    echo "00000000-0000-0000-0000-000000000000"
    exit 0
elif [ "$1" = "group" ] && [ "$2" = "exists" ]; then
    echo "true"
    exit 0
elif [ "$1" = "deployment" ] && [ "$2" = "group" ] && [ "$3" = "what-if" ]; then
    echo "Error: Bicep template syntax invalid" >&2
    exit 1
fi
EOF
chmod +x "${MOCK_BIN}/az"

OUT_6=$(PATH="${MOCK_BIN}:$PATH" ./deploy/scripts/deploy.sh 2>&1 || true)
if echo "${OUT_6}" | grep -q "ERROR: Bicep What-If preview failed"; then
    assert_test "What-If failure halts deployment before Stage 4 authorization" 0
else
    assert_test "What-If failure halts deployment before Stage 4 authorization" 1
fi

# ------------------------------------------------------------------------------
# Test 7: Teardown dry-run verifies non-destructive behavior
# ------------------------------------------------------------------------------
OUT_7=$(./deploy/scripts/teardown.sh demo --dry-run 2>&1 || true)
if echo "${OUT_7}" | grep -q "\[DRY-RUN MODE ACTIVATED\]" && echo "${OUT_7}" | grep -q "NO CHANGES HAVE BEEN MADE"; then
    assert_test "teardown.sh --dry-run confirms non-destructive execution" 0
else
    assert_test "teardown.sh --dry-run confirms non-destructive execution" 1
fi

# ------------------------------------------------------------------------------
# Test 8: Smoke test classifies absent dead-letter deliveries as SKIPPED
# ------------------------------------------------------------------------------
cat << 'EOF' > "${MOCK_BIN}/curl"
#!/usr/bin/env bash
URL=""
for arg in "$@"; do
    if [[ "$arg" == http://* ]] || [[ "$arg" == https://* ]]; then
        URL="$arg"
        break
    fi
done

if [[ "$URL" == *"/health/"* ]]; then
    echo -e '{"status":"Healthy"}\n200'
elif [[ "$URL" == *"/v1/events/"* ]]; then
    echo -e '{"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","status":"Processed"}\n200'
elif [[ "$URL" == *"/v1/events"* ]]; then
    echo -e '{"eventId":"3fa85f64-5717-4562-b3fc-2c963f66afa6","status":"Accepted"}\n202'
elif [[ "$URL" == *"/v1/endpoints"* ]]; then
    echo -e '{"id":"4fa85f64-5717-4562-b3fc-2c963f66afa6"}\n201'
elif [[ "$URL" == *"/v1/deliveries?status=DeadLettered"* ]]; then
    echo -e '{"items":[],"totalCount":0}\n200'
elif [[ "$URL" == *"/v1/deliveries"* ]]; then
    echo -e '{"items":[{"id":"5fa85f64-5717-4562-b3fc-2c963f66afa6","status":"Success"}],"totalCount":1}\n200'
else
    echo -e '{"error":"not_found"}\n404'
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/curl"

OUT_8=$(PATH="${MOCK_BIN}:$PATH" RELAY_SMOKE_API_KEY="rl_live_mock_secret_key" ./deploy/scripts/smoke-test.sh 2>&1 || true)
if echo "${OUT_8}" | grep -q "\[SKIPPED\] Manual Dead-Letter Replay" && echo "${OUT_8}" | grep -q "ALL MANDATORY SMOKE TESTS COMPLETED SUCCESSFULLY"; then
    assert_test "Smoke test marks optional replay as SKIPPED and completes mandatory tests" 0
else
    assert_test "Smoke test marks optional replay as SKIPPED and completes mandatory tests" 1
fi

# ------------------------------------------------------------------------------
# Test 9: Smoke test never leaks API key in output
# ------------------------------------------------------------------------------
SECRET_KEY="rl_live_secret_canary_key_9988776655"
OUT_9=$(PATH="${MOCK_BIN}:$PATH" RELAY_SMOKE_API_KEY="${SECRET_KEY}" ./deploy/scripts/smoke-test.sh 2>&1 || true)
if echo "${OUT_9}" | grep -q "${SECRET_KEY}"; then
    assert_test "Smoke test zero secret leakage invariant" 1
else
    assert_test "Smoke test zero secret leakage invariant" 0
fi

echo "=============================================================================="
echo "TEST SUMMARY: Passed: ${PASS_COUNT}, Failed: ${FAIL_COUNT}"
echo "=============================================================================="

if [ "${FAIL_COUNT}" -gt 0 ]; then
    exit 1
fi
exit 0
