#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Relay Deployment Scripts Behavioral & Safety Verification Suite
# ==============================================================================
# Invariants verified:
# 1. Expected exit codes for all safety boundaries.
# 2. Expected output classification (PASS, FAIL, SKIPPED, BLOCKER, etc.).
# 3. Absolute safety invariant: ZERO forbidden cloud mutations invoked.
# ==============================================================================

TEST_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TMP_DIR=$(mktemp -d)
trap 'rm -rf "${TMP_DIR}"' EXIT

MOCK_BIN="${TMP_DIR}/bin"
AZ_LOG="${TMP_DIR}/az_invocations.log"
mkdir -p "${MOCK_BIN}"

PASS_COUNT=0
FAIL_COUNT=0

assert_test() {
    local name="$1"
    local actual_code="$2"
    local expected_code="$3"
    local output="$4"
    local expected_pattern="$5"

    local passed=true

    if [ "${actual_code}" -ne "${expected_code}" ]; then
        echo "  [FAIL] ${name} (Expected exit code ${expected_code}, got ${actual_code})"
        passed=false
    elif ! echo "${output}" | grep -qE "${expected_pattern}"; then
        echo "  [FAIL] ${name} (Output did not match expected pattern '${expected_pattern}')"
        passed=false
    fi

    # Mutation invariant check
    if [ -f "${AZ_LOG}" ]; then
        if grep -qE "group create|deployment group create|containerapp update|containerapp job update|containerapp job start|storage blob upload-batch" "${AZ_LOG}"; then
            echo "  [FAIL] ${name} (CRITICAL: Forbidden cloud mutation command was logged in az_invocations.log!)"
            passed=false
        fi
    fi

    if [ "${passed}" = true ]; then
        echo "  [PASS] ${name}"
        PASS_COUNT=$((PASS_COUNT + 1))
    else
        FAIL_COUNT=$((FAIL_COUNT + 1))
    fi
}

echo "=============================================================================="
echo "RUNNING SCRIPT BEHAVIOURAL & SAFETY INVARIANT TESTS"
echo "=============================================================================="

# ------------------------------------------------------------------------------
# Test 1: Bicep compilation failure produces a nonzero dry-run
# ------------------------------------------------------------------------------
cat << 'EOF' > "${MOCK_BIN}/az"
#!/usr/bin/env bash
echo "az $*" >> "${AZ_LOG}"
if [ "$1" = "bicep" ] && [ "$2" = "build" ]; then
    echo "Error BCP001: Syntax error in Bicep template" >&2
    exit 1
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/az"
: > "${AZ_LOG}"

OUT_1=$(PATH="${MOCK_BIN}:$PATH" "${TEST_DIR}/deploy/scripts/deploy.sh" --dry-run 2>&1) || CODE_1=$?
assert_test "Bicep compilation failure produces a nonzero dry-run" "${CODE_1:-0}" 1 "${OUT_1}" "ERROR: Bicep compilation failed"

# ------------------------------------------------------------------------------
# Test 2: Missing parameter file does not report success
# ------------------------------------------------------------------------------
: > "${AZ_LOG}"
OUT_2=$(PATH="${MOCK_BIN}:$PATH" "${TEST_DIR}/deploy/scripts/deploy.sh" nonexistent-env --dry-run 2>&1) || CODE_2=$?
assert_test "Missing parameter file does not report success" "${CODE_2:-0}" 1 "${OUT_2}" "ERROR: Required parameter file '.*' was not found"

# ------------------------------------------------------------------------------
# Test 3: Missing target subscription ID blocks preflight
# ------------------------------------------------------------------------------
: > "${AZ_LOG}"
OUT_3=$(PATH="${MOCK_BIN}:$PATH" AZURE_SUBSCRIPTION_ID="" "${TEST_DIR}/deploy/scripts/deploy.sh" --preflight 2>&1) || CODE_3=$?
assert_test "Missing target subscription ID blocks preflight" "${CODE_3:-0}" 1 "${OUT_3}" "Target subscription ID is mandatory"

# ------------------------------------------------------------------------------
# Test 4: Mismatched subscription ID blocks execution
# ------------------------------------------------------------------------------
cat << 'EOF' > "${MOCK_BIN}/az"
#!/usr/bin/env bash
echo "az $*" >> "${AZ_LOG}"
if [ "$1" = "account" ] && [ "$2" = "show" ]; then
    echo '{"id":"actual-sub-1111","name":"Production Sub","state":"Enabled","user":{"name":"dev@example.com"}}'
    exit 0
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/az"
: > "${AZ_LOG}"

OUT_4=$(PATH="${MOCK_BIN}:$PATH" "${TEST_DIR}/deploy/scripts/deploy.sh" --subscription=expected-sub-9999 --preflight 2>&1) || CODE_4=$?
assert_test "Mismatched subscription ID blocks execution" "${CODE_4:-0}" 1 "${OUT_4}" "Target subscription ID does not match active Azure CLI subscription"

# ------------------------------------------------------------------------------
# Test 5: Missing provider registration produces nonzero preflight
# ------------------------------------------------------------------------------
cat << 'EOF' > "${MOCK_BIN}/az"
#!/usr/bin/env bash
echo "az $*" >> "${AZ_LOG}"
if [ "$1" = "account" ] && [ "$2" = "show" ]; then
    echo '{"id":"sub-1111","name":"Test Sub","state":"Enabled","user":{"name":"dev@example.com"}}'
    exit 0
elif [ "$1" = "ad" ]; then
    echo "00000000-0000-0000-0000-000000000000"
    exit 0
elif [ "$1" = "provider" ] && [ "$2" = "show" ]; then
    if [ "$4" = "Microsoft.ManagedIdentity" ]; then
        echo "NotRegistered"
    else
        echo "Registered"
    fi
    exit 0
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/az"
: > "${AZ_LOG}"

OUT_5=$(PATH="${MOCK_BIN}:$PATH" "${TEST_DIR}/deploy/scripts/deploy.sh" --subscription=sub-1111 --preflight 2>&1) || CODE_5=$?
assert_test "Missing provider registration produces nonzero preflight" "${CODE_5:-0}" 1 "${OUT_5}" "Microsoft.ManagedIdentity is not registered"

# ------------------------------------------------------------------------------
# Test 6: Missing SQL Basic availability prevents a passed preflight
# ------------------------------------------------------------------------------
cat << 'EOF' > "${MOCK_BIN}/az"
#!/usr/bin/env bash
echo "az $*" >> "${AZ_LOG}"
if [ "$1" = "account" ] && [ "$2" = "show" ]; then
    echo '{"id":"sub-1111","name":"Test Sub","state":"Enabled","user":{"name":"dev@example.com"}}'
    exit 0
elif [ "$1" = "ad" ]; then
    echo "00000000-0000-0000-0000-000000000000"
    exit 0
elif [ "$1" = "provider" ] && [ "$2" = "show" ]; then
    echo "Registered"
    exit 0
elif [ "$1" = "sql" ] && [ "$2" = "db" ] && [ "$3" = "list-editions" ]; then
    echo "Standard GeneralPurpose"
    exit 0
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/az"
: > "${AZ_LOG}"

OUT_6=$(PATH="${MOCK_BIN}:$PATH" "${TEST_DIR}/deploy/scripts/deploy.sh" --subscription=sub-1111 --preflight 2>&1) || CODE_6=$?
assert_test "Missing SQL Basic availability prevents a passed preflight" "${CODE_6:-0}" 1 "${OUT_6}" "Azure SQL Basic edition availability could not be verified"

# ------------------------------------------------------------------------------
# Test 7: Declining resource-group preparation does not create the group
# ------------------------------------------------------------------------------
cat << 'EOF' > "${MOCK_BIN}/az"
#!/usr/bin/env bash
echo "az $*" >> "${AZ_LOG}"
if [ "$1" = "account" ] && [ "$2" = "show" ]; then
    echo '{"id":"sub-1111","name":"Test Sub","state":"Enabled","user":{"name":"dev@example.com"}}'
    exit 0
elif [ "$1" = "ad" ]; then
    echo "00000000-0000-0000-0000-000000000000"
    exit 0
elif [ "$1" = "provider" ] && [ "$2" = "show" ]; then
    if [ "$2" = "show" ] && [[ "$*" == *"--query registrationState"* ]]; then
        echo "Registered"
    else
        echo "centralindia"
    fi
    exit 0
elif [ "$1" = "sql" ] && [ "$2" = "db" ] && [ "$3" = "list-editions" ]; then
    echo "Basic Standard"
    exit 0
elif [ "$1" = "group" ] && [ "$2" = "exists" ]; then
    echo "false"
    exit 0
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/az"
: > "${AZ_LOG}"

OUT_7=$(printf "no\n" | PATH="${MOCK_BIN}:$PATH" "${TEST_DIR}/deploy/scripts/deploy.sh" --subscription=sub-1111 2>&1) || CODE_7=$?
assert_test "Declining resource-group preparation does not create the group" "${CODE_7:-0}" 0 "${OUT_7}" "Deployment aborted by operator"

# ------------------------------------------------------------------------------
# Test 8: What-If failure stops before live deployment confirmation
# ------------------------------------------------------------------------------
cat << 'EOF' > "${MOCK_BIN}/az"
#!/usr/bin/env bash
echo "az $*" >> "${AZ_LOG}"
if [ "$1" = "account" ] && [ "$2" = "show" ]; then
    echo '{"id":"sub-1111","name":"Test Sub","state":"Enabled","user":{"name":"dev@example.com"}}'
    exit 0
elif [ "$1" = "ad" ]; then
    echo "00000000-0000-0000-0000-000000000000"
    exit 0
elif [ "$1" = "provider" ] && [ "$2" = "show" ]; then
    if [[ "$*" == *"--query registrationState"* ]]; then
        echo "Registered"
    else
        echo "centralindia"
    fi
    exit 0
elif [ "$1" = "sql" ] && [ "$2" = "db" ] && [ "$3" = "list-editions" ]; then
    echo "Basic Standard"
    exit 0
elif [ "$1" = "group" ] && [ "$2" = "exists" ]; then
    echo "true"
    exit 0
elif [ "$1" = "deployment" ] && [ "$2" = "group" ] && [ "$3" = "what-if" ]; then
    echo "ERROR: ARM internal template evaluation failure" >&2
    exit 1
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/az"
: > "${AZ_LOG}"

OUT_8=$(PATH="${MOCK_BIN}:$PATH" "${TEST_DIR}/deploy/scripts/deploy.sh" --subscription=sub-1111 2>&1) || CODE_8=$?
assert_test "What-If failure stops before live deployment confirmation" "${CODE_8:-0}" 1 "${OUT_8}" "Refusing to proceed with live deployment without a validated What-If preview"

# ------------------------------------------------------------------------------
# Test 9: Declining final deployment confirmation never calls deployment group create
# ------------------------------------------------------------------------------
cat << 'EOF' > "${MOCK_BIN}/az"
#!/usr/bin/env bash
echo "az $*" >> "${AZ_LOG}"
if [ "$1" = "account" ] && [ "$2" = "show" ]; then
    echo '{"id":"sub-1111","name":"Test Sub","state":"Enabled","user":{"name":"dev@example.com"}}'
    exit 0
elif [ "$1" = "ad" ]; then
    echo "00000000-0000-0000-0000-000000000000"
    exit 0
elif [ "$1" = "provider" ] && [ "$2" = "show" ]; then
    if [[ "$*" == *"--query registrationState"* ]]; then
        echo "Registered"
    else
        echo "centralindia"
    fi
    exit 0
elif [ "$1" = "sql" ] && [ "$2" = "db" ] && [ "$3" = "list-editions" ]; then
    echo "Basic Standard"
    exit 0
elif [ "$1" = "group" ] && [ "$2" = "exists" ]; then
    echo "true"
    exit 0
elif [ "$1" = "deployment" ] && [ "$2" = "group" ] && [ "$3" = "what-if" ]; then
    echo "What-If preview evaluated successfully."
    exit 0
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/az"
: > "${AZ_LOG}"

OUT_9=$(printf "no\n" | PATH="${MOCK_BIN}:$PATH" "${TEST_DIR}/deploy/scripts/deploy.sh" --subscription=sub-1111 2>&1) || CODE_9=$?
assert_test "Declining final deployment confirmation never calls deployment group create" "${CODE_9:-0}" 0 "${OUT_9}" "Deployment aborted by operator"

# ------------------------------------------------------------------------------
# Test 10: Failed migration execution prevents API/Worker revision updates
# ------------------------------------------------------------------------------
cat << 'EOF' > "${MOCK_BIN}/az"
#!/usr/bin/env bash
echo "az $*" >> "${AZ_LOG}"
if [ "$1" = "account" ] && [ "$2" = "show" ]; then
    echo '{"id":"sub-1111","name":"Test Sub","state":"Enabled","user":{"name":"dev@example.com"}}'
    exit 0
elif [ "$1" = "ad" ]; then
    echo "00000000-0000-0000-0000-000000000000"
    exit 0
elif [ "$1" = "provider" ] && [ "$2" = "show" ]; then
    if [[ "$*" == *"--query registrationState"* ]]; then
        echo "Registered"
    else
        echo "centralindia"
    fi
    exit 0
elif [ "$1" = "sql" ] && [ "$2" = "db" ] && [ "$3" = "list-editions" ]; then
    echo "Basic Standard"
    exit 0
elif [ "$1" = "group" ] && [ "$2" = "exists" ]; then
    echo "true"
    exit 0
elif [ "$1" = "deployment" ] && [ "$2" = "group" ] && [ "$3" = "what-if" ]; then
    exit 0
elif [ "$1" = "deployment" ] && [ "$2" = "group" ] && [ "$3" = "create" ]; then
    echo '{"apiUrl":{"value":"http://localhost:5000"},"acrName":{"value":"acrrelay"},"acrLoginServer":{"value":"acrrelay.azurecr.io"},"frontendWebEndpoint":{"value":"http://web"},"frontendStorageAccountName":{"value":"strelayweb"},"sqlServerFqdn":{"value":"sql.database.windows.net"},"sqlDatabaseName":{"value":"sqldb"},"migrationJobName":{"value":"ca-relay-migration"}}'
    exit 0
elif [ "$1" = "acr" ] && [ "$2" = "login" ]; then
    exit 0
elif [ "$1" = "containerapp" ] && [ "$2" = "job" ] && [ "$3" = "update" ]; then
    exit 0
elif [ "$1" = "containerapp" ] && [ "$2" = "job" ] && [ "$3" = "start" ]; then
    echo "migration-run-1234"
    exit 0
elif [ "$1" = "containerapp" ] && [ "$2" = "job" ] && [ "$3" = "execution" ]; then
    echo "Failed"
    exit 0
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/az"

cat << 'EOF' > "${MOCK_BIN}/podman"
#!/usr/bin/env bash
exit 0
EOF
chmod +x "${MOCK_BIN}/podman"

cat << 'EOF' > "${MOCK_BIN}/dotnet"
#!/usr/bin/env bash
exit 0
EOF
chmod +x "${MOCK_BIN}/dotnet"

MIGRATION_LOG="${TMP_DIR}/az_migration.log"
cat << EOF > "${MOCK_BIN}/az_log_wrapper"
#!/usr/bin/env bash
echo "az \$*" >> "${MIGRATION_LOG}"
exec "${MOCK_BIN}/az" "\$@"
EOF
chmod +x "${MOCK_BIN}/az_log_wrapper"

# Run with az pointed to wrapper
OUT_10=$(printf "yes\n" | PATH="${MOCK_BIN}:$PATH" "${TEST_DIR}/deploy/scripts/deploy.sh" --subscription=sub-1111 2>&1) || CODE_10=$?
if [ "${CODE_10:-0}" -eq 1 ] && echo "${OUT_10}" | grep -q "Migration job execution 'migration-run-1234' failed" && ! grep -q "containerapp update" "${AZ_LOG}"; then
    echo "  [PASS] Failed migration execution prevents API/Worker revision updates"
    PASS_COUNT=$((PASS_COUNT + 1))
else
    echo "  [FAIL] Failed migration execution prevents API/Worker revision updates"
    FAIL_COUNT=$((FAIL_COUNT + 1))
fi

# ------------------------------------------------------------------------------
# Test 11: Missing API key stops smoke testing
# ------------------------------------------------------------------------------
OUT_11=$(RELAY_SMOKE_API_KEY="" "${TEST_DIR}/deploy/scripts/smoke-test.sh" 2>&1) || CODE_11=$?
assert_test "Missing API key stops smoke testing" "${CODE_11:-0}" 1 "${OUT_11}" "RELAY_SMOKE_API_KEY environment variable is required"

# ------------------------------------------------------------------------------
# Test 12: HTTP failure makes affected mandatory smoke test fail
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
elif [[ "$URL" == *"/v1/events"* ]]; then
    echo -e '{"error":"Internal Error"}\n500'
else
    echo -e '{"status":"ok"}\n200'
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/curl"

OUT_12=$(PATH="${MOCK_BIN}:$PATH" RELAY_SMOKE_API_KEY="rl_live_test_key" "${TEST_DIR}/deploy/scripts/smoke-test.sh" 2>&1) || CODE_12=$?
assert_test "HTTP failure makes affected mandatory smoke test fail" "${CODE_12:-0}" 1 "${OUT_12}" "Expected HTTP 202, got HTTP 500"

# ------------------------------------------------------------------------------
# Test 13: Delivery remaining pending does not count as successful delivery
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
elif [[ "$URL" == *"/v1/events"* ]]; then
    echo -e '{"eventId":"evt-smoke-test-100","status":"Accepted"}\n202'
elif [[ "$URL" == *"/v1/endpoints"* ]]; then
    echo -e '{"id":"ep-200"}\n201'
elif [[ "$URL" == *"/v1/deliveries?endpointId="* ]]; then
    echo -e '{"items":[{"id":"del-300","eventId":"evt-smoke-test-100","status":"Pending"}],"totalCount":1}\n200'
else
    echo -e '{"error":"not_found"}\n404'
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/curl"

OUT_13=$(PATH="${MOCK_BIN}:$PATH" RELAY_SMOKE_API_KEY="rl_live_test_key" "${TEST_DIR}/deploy/scripts/smoke-test.sh" 2>&1) || CODE_13=$?
assert_test "Delivery remaining pending does not count as successful delivery" "${CODE_13:-0}" 1 "${OUT_13}" "Delivery did not reach 'Delivered' state within timeout"

# ------------------------------------------------------------------------------
# Test 14: Delivery with unrelated event ID cannot satisfy Test 5
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
elif [[ "$URL" == *"/v1/events"* ]]; then
    echo -e '{"eventId":"evt-smoke-test-matching-1","status":"Accepted"}\n202'
elif [[ "$URL" == *"/v1/endpoints"* ]]; then
    echo -e '{"id":"ep-200"}\n201'
elif [[ "$URL" == *"/v1/deliveries?endpointId="* ]]; then
    # Return a delivered record, but for an UNRELATED event ID!
    echo -e '{"items":[{"id":"del-300","eventId":"evt-unrelated-999","status":"Delivered"}],"totalCount":1}\n200'
else
    echo -e '{"error":"not_found"}\n404'
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/curl"

OUT_14=$(PATH="${MOCK_BIN}:$PATH" RELAY_SMOKE_API_KEY="rl_live_test_key" "${TEST_DIR}/deploy/scripts/smoke-test.sh" 2>&1) || CODE_14=$?
assert_test "Delivery with unrelated event ID cannot satisfy Test 5" "${CODE_14:-0}" 1 "${OUT_14}" "Delivery did not reach 'Delivered' state within timeout"

# ------------------------------------------------------------------------------
# Test 15: Optional replay is reported as SKIPPED only when no eligible record exists
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
    echo -e '{"id":"evt-smoke-test-777","status":"Processed"}\n200'
elif [[ "$URL" == *"/v1/events"* ]]; then
    echo -e '{"eventId":"evt-smoke-test-777","status":"Accepted"}\n202'
elif [[ "$URL" == *"/v1/endpoints"* ]]; then
    echo -e '{"id":"ep-200"}\n201'
elif [[ "$URL" == *"/v1/deliveries?status=DeadLettered"* ]]; then
    echo -e '{"items":[],"totalCount":0}\n200'
elif [[ "$URL" == *"/v1/deliveries?endpointId="* ]]; then
    echo -e '{"items":[{"id":"del-300","eventId":"evt-smoke-test-777","status":"Delivered"}],"totalCount":1}\n200'
else
    echo -e '{"error":"not_found"}\n404'
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/curl"

OUT_15=$(PATH="${MOCK_BIN}:$PATH" RELAY_SMOKE_API_KEY="rl_live_test_key" "${TEST_DIR}/deploy/scripts/smoke-test.sh" 2>&1) || CODE_15=$?
assert_test "Optional replay reported as SKIPPED when no eligible records exist" "${CODE_15:-0}" 0 "${OUT_15}" "\\[SKIPPED\\] Manual Dead-Letter Replay & Replay Idempotency"

# ------------------------------------------------------------------------------
# Test 16: Replayed response idempotency is actually checked
# ------------------------------------------------------------------------------
# Subcase A: Matching auditId and replayCount succeeds
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
    echo -e '{"id":"evt-smoke-test-888","status":"Processed"}\n200'
elif [[ "$URL" == *"/v1/events"* ]]; then
    echo -e '{"eventId":"evt-smoke-test-888","status":"Accepted"}\n202'
elif [[ "$URL" == *"/v1/endpoints"* ]]; then
    echo -e '{"id":"ep-200"}\n201'
elif [[ "$URL" == *"/v1/deliveries?status=DeadLettered"* ]]; then
    echo -e '{"items":[{"id":"del-dead-1","status":"DeadLettered"}],"totalCount":1}\n200'
elif [[ "$URL" == *"/v1/deliveries/del-dead-1/replay"* ]]; then
    echo -e '{"auditId":"aud-consistent-1","deliveryId":"del-dead-1","replayCount":1}\n202'
elif [[ "$URL" == *"/v1/deliveries?endpointId="* ]]; then
    echo -e '{"items":[{"id":"del-300","eventId":"evt-smoke-test-888","status":"Delivered"}],"totalCount":1}\n200'
else
    echo -e '{"error":"not_found"}\n404'
fi
exit 0
EOF
chmod +x "${MOCK_BIN}/curl"

OUT_16=$(PATH="${MOCK_BIN}:$PATH" RELAY_SMOKE_API_KEY="rl_live_test_key" "${TEST_DIR}/deploy/scripts/smoke-test.sh" 2>&1) || CODE_16=$?
assert_test "Replayed response idempotency passes with matching audit identifiers" "${CODE_16:-0}" 0 "${OUT_16}" "Replay Idempotency Confirmed: auditId=aud-consistent-1, replayCount=1"

# ------------------------------------------------------------------------------
# Test 17: Smoke test zero secret leakage invariant
# ------------------------------------------------------------------------------
SECRET_CANARY="rl_live_secret_canary_key_998877665544332211"
OUT_17=$(PATH="${MOCK_BIN}:$PATH" RELAY_SMOKE_API_KEY="${SECRET_CANARY}" "${TEST_DIR}/deploy/scripts/smoke-test.sh" 2>&1) || true
if echo "${OUT_17}" | grep -q "${SECRET_CANARY}"; then
    echo "  [FAIL] Smoke test zero secret leakage invariant (Canary was leaked in output)"
    FAIL_COUNT=$((FAIL_COUNT + 1))
else
    echo "  [PASS] Smoke test zero secret leakage invariant"
    PASS_COUNT=$((PASS_COUNT + 1))
fi

echo "=============================================================================="
echo "TEST SUMMARY: Passed: ${PASS_COUNT}, Failed: ${FAIL_COUNT}"
echo "=============================================================================="

if [ "${FAIL_COUNT}" -gt 0 ]; then
    exit 1
fi
exit 0
