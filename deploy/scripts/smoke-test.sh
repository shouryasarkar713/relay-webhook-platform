#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Relay End-to-End Production Smoke Test Suite (Acceptance Verification)
# ==============================================================================
# Invariants:
# 1. Requires explicit RELAY_SMOKE_API_KEY; never uses fake/default fallback keys.
# 2. Never prints, logs, or persists the API key or its prefix.
# 3. Avoids passing secrets as visible process arguments (uses curl --config).
# 4. Uses finite connection (10s) and request (30s) timeouts on all HTTP calls.
# 5. Validates HTTP status codes and JSON payloads before asserting properties.
# 6. Test 5 validates that the delivery matching the exact submitted event ID
#    reaches the final domain model success state ('Delivered').
# 7. Test 6 validates event ID equality and valid JSON payload structure.
# 8. Test 7 (Dead-Letter Replay) is explicitly optional; marked SKIPPED when
#    no dead-letter records exist, and verifies replay response idempotency when present.
# 9. Distinguishes PASS, FAIL, and SKIPPED tests.
# 10. Exits nonzero if any mandatory acceptance test fails.
# ==============================================================================

API_BASE_URL="${1:-http://localhost:5000}"
RECEIVER_URL="${2:-https://httpbin.org/post}"

# Strip trailing slashes
API_BASE_URL="${API_BASE_URL%/}"

echo "=============================================================================="
echo "RELAY PRODUCTION SMOKE ACCEPTANCE SUITE"
echo "Target API:      ${API_BASE_URL}"
echo "Target Receiver: ${RECEIVER_URL}"
echo "=============================================================================="

# 1. Enforce API Key Presence
if [ -z "${RELAY_SMOKE_API_KEY:-}" ]; then
    echo "ERROR: RELAY_SMOKE_API_KEY environment variable is required but not set."
    echo ""
    echo "To generate a tenant and authenticated API key before running smoke tests:"
    echo "  dotnet run --project src/Relay.Admin/Relay.Admin.csproj -- tenant create --name 'Smoke Tenant'"
    echo "  dotnet run --project src/Relay.Admin/Relay.Admin.csproj -- apikey create --tenant-id <TENANT_ID> --name 'Smoke Key'"
    echo "Then export the plain-text key:"
    echo "  export RELAY_SMOKE_API_KEY='rl_live_...'"
    exit 1
fi

CURL_TIMEOUTS=(--connect-timeout 10 --max-time 30)

# Tracking counters
TESTS_TOTAL=0
TESTS_PASSED=0
TESTS_FAILED=0
TESTS_SKIPPED=0
declare -a TEST_RESULTS=()

record_result() {
    local name="$1"
    local status="$2"
    local detail="${3:-}"
    TESTS_TOTAL=$((TESTS_TOTAL + 1))
    if [ "$status" = "PASS" ]; then
        TESTS_PASSED=$((TESTS_PASSED + 1))
        TEST_RESULTS+=("  [PASS]    ${name}")
        echo "  --> RESULT: PASS"
    elif [ "$status" = "SKIPPED" ]; then
        TESTS_SKIPPED=$((TESTS_SKIPPED + 1))
        TEST_RESULTS+=("  [SKIPPED] ${name} (${detail})")
        echo "  --> RESULT: SKIPPED (${detail})"
    else
        TESTS_FAILED=$((TESTS_FAILED + 1))
        TEST_RESULTS+=("  [FAIL]    ${name} (${detail})")
        echo "  --> RESULT: FAIL (${detail})"
    fi
    echo ""
}

# Helper: execute curl securely, passing API key via stdin configuration
http_request() {
    local method="$1"
    local url="$2"
    local use_auth="${3:-true}"
    shift 3 || shift 2
    local extra_args=("$@")

    local raw_output
    if [ "${use_auth}" = "true" ]; then
        if ! raw_output=$(curl -sS "${CURL_TIMEOUTS[@]}" \
            --config <(printf 'header = "X-Api-Key: %s"\n' "${RELAY_SMOKE_API_KEY}") \
            -w "\n%{http_code}" -X "$method" "$url" "${extra_args[@]}"); then
            HTTP_CODE="000"
            RESP_BODY=""
            return 1
        fi
    else
        if ! raw_output=$(curl -sS "${CURL_TIMEOUTS[@]}" \
            -w "\n%{http_code}" -X "$method" "$url" "${extra_args[@]}"); then
            HTTP_CODE="000"
            RESP_BODY=""
            return 1
        fi
    fi

    HTTP_CODE=$(echo "$raw_output" | tail -n 1)
    RESP_BODY=$(echo "$raw_output" | sed '$d')
    return 0
}

# ------------------------------------------------------------------------------
# Test 1: API Availability & Health Probes
# ------------------------------------------------------------------------------
echo "[TEST 1] Verifying API Liveness & Readiness Probes..."
TEST_1_NAME="API Liveness & Readiness Probes"
if http_request GET "${API_BASE_URL}/health/live" false && [ "${HTTP_CODE}" = "200" ]; then
    LIVE_OK=true
else
    LIVE_OK=false
fi

if http_request GET "${API_BASE_URL}/health/ready" false && [ "${HTTP_CODE}" = "200" ]; then
    READY_OK=true
else
    READY_OK=false
fi

if [ "${LIVE_OK}" = true ] && [ "${READY_OK}" = true ]; then
    record_result "${TEST_1_NAME}" "PASS"
else
    record_result "${TEST_1_NAME}" "FAIL" "Liveness HTTP=${LIVE_OK}, Readiness HTTP=${READY_OK}"
fi

# ------------------------------------------------------------------------------
# Test 2: Event Ingestion (POST /v1/events)
# ------------------------------------------------------------------------------
echo "[TEST 2] Submitting Event via POST /v1/events..."
TEST_2_NAME="Authenticated Event Ingestion (POST /v1/events)"
IDEM_KEY_1="smoke-idem-$(date +%s)-$RANDOM"
EVENT_PAYLOAD='{"eventType":"smoke.invoice.paid","payload":{"invoiceNumber":"INV-SMOKE-001","amount":99.50,"currency":"USD"}}'

if http_request POST "${API_BASE_URL}/v1/events" true \
    -H "Idempotency-Key: ${IDEM_KEY_1}" \
    -H "Content-Type: application/json" \
    -d "${EVENT_PAYLOAD}" && [ "${HTTP_CODE}" = "202" ]; then
    
    EVENT_ID_1=$(echo "${RESP_BODY}" | jq -r .eventId 2>/dev/null || echo "")
    if [ -n "${EVENT_ID_1}" ] && [ "${EVENT_ID_1}" != "null" ]; then
        echo "  Ingested Event ID: ${EVENT_ID_1}"
        record_result "${TEST_2_NAME}" "PASS"
    else
        record_result "${TEST_2_NAME}" "FAIL" "HTTP 202 returned but eventId missing from JSON body"
    fi
else
    record_result "${TEST_2_NAME}" "FAIL" "Expected HTTP 202, got HTTP ${HTTP_CODE}"
fi

# ------------------------------------------------------------------------------
# Test 3: Idempotency Protection
# ------------------------------------------------------------------------------
echo "[TEST 3] Verifying Duplicate Request Idempotency..."
TEST_3_NAME="Duplicate Request Idempotency Enforcement"
if [ -n "${EVENT_ID_1:-}" ] && [ "${EVENT_ID_1}" != "null" ]; then
    if http_request POST "${API_BASE_URL}/v1/events" true \
        -H "Idempotency-Key: ${IDEM_KEY_1}" \
        -H "Content-Type: application/json" \
        -d "${EVENT_PAYLOAD}" && [ "${HTTP_CODE}" = "202" ]; then
        
        DUP_EVENT_ID=$(echo "${RESP_BODY}" | jq -r .eventId 2>/dev/null || echo "")
        if [ "${EVENT_ID_1}" = "${DUP_EVENT_ID}" ]; then
            echo "  Idempotency Verified: Duplicate submission returned identical EventId: ${DUP_EVENT_ID}"
            record_result "${TEST_3_NAME}" "PASS"
        else
            record_result "${TEST_3_NAME}" "FAIL" "Original ID (${EVENT_ID_1}) != Duplicate ID (${DUP_EVENT_ID})"
        fi
    else
        record_result "${TEST_3_NAME}" "FAIL" "Expected HTTP 202 on duplicate submission, got HTTP ${HTTP_CODE}"
    fi
else
    record_result "${TEST_3_NAME}" "FAIL" "Prerequisite Test 2 failed; cannot test duplicate submission"
fi

# ------------------------------------------------------------------------------
# Test 4: Webhook Endpoint Registration (POST /v1/endpoints)
# ------------------------------------------------------------------------------
echo "[TEST 4] Registering Webhook Destination Endpoint..."
TEST_4_NAME="Webhook Endpoint Registration (POST /v1/endpoints)"
ENDPOINT_PAYLOAD=$(jq -n \
    --arg name "Smoke Acceptance Receiver ($RANDOM)" \
    --arg url "${RECEIVER_URL}" \
    '{name: $name, url: $url, subscribedEventTypes: ["smoke.dispatch.test"]}')

if http_request POST "${API_BASE_URL}/v1/endpoints" true \
    -H "Content-Type: application/json" \
    -d "${ENDPOINT_PAYLOAD}" && [ "${HTTP_CODE}" = "201" ]; then
    
    ENDPOINT_ID=$(echo "${RESP_BODY}" | jq -r .id 2>/dev/null || echo "")
    if [ -n "${ENDPOINT_ID}" ] && [ "${ENDPOINT_ID}" != "null" ]; then
        echo "  Registered Endpoint ID: ${ENDPOINT_ID}"
        record_result "${TEST_4_NAME}" "PASS"
    else
        record_result "${TEST_4_NAME}" "FAIL" "HTTP 201 returned but endpoint id missing from JSON body"
    fi
else
    record_result "${TEST_4_NAME}" "FAIL" "Expected HTTP 201, got HTTP ${HTTP_CODE}"
fi

# ------------------------------------------------------------------------------
# Test 5: End-to-End Fan-out & Successful Delivery Verification
# ------------------------------------------------------------------------------
echo "[TEST 5] Submitting Matching Event & Polling Delivery Dispatch..."
TEST_5_NAME="End-to-End Outbox Fan-out & Delivery Record"
if [ -n "${ENDPOINT_ID:-}" ] && [ "${ENDPOINT_ID}" != "null" ]; then
    IDEM_KEY_2="smoke-dispatch-$(date +%s)-$RANDOM"
    DISPATCH_PAYLOAD='{"eventType":"smoke.dispatch.test","payload":{"action":"verify_delivery","timestamp":"'$(date -u +%Y-%m-%dT%H:%M:%SZ)'"}}'

    if http_request POST "${API_BASE_URL}/v1/events" true \
        -H "Idempotency-Key: ${IDEM_KEY_2}" \
        -H "Content-Type: application/json" \
        -d "${DISPATCH_PAYLOAD}" && [ "${HTTP_CODE}" = "202" ]; then
        
        EVENT_ID_2=$(echo "${RESP_BODY}" | jq -r .eventId 2>/dev/null || echo "")
        echo "  Ingested Matching Event ID: ${EVENT_ID_2}"
        echo "  Polling /v1/deliveries for endpoint ${ENDPOINT_ID} matching event ${EVENT_ID_2} (bounded timeout: 30s)..."

        DELIVERY_SUCCESS=false
        LAST_SEEN_STATUS="None"

        for i in $(seq 1 6); do
            sleep 5
            if http_request GET "${API_BASE_URL}/v1/deliveries?endpointId=${ENDPOINT_ID}" true && [ "${HTTP_CODE}" = "200" ]; then
                # Locate delivery specifically matching EVENT_ID_2
                MATCHING_ITEM=$(echo "${RESP_BODY}" | jq -c --arg eid "${EVENT_ID_2}" '.items[]? | select(.eventId == $eid)' 2>/dev/null | head -n 1)
                if [ -n "${MATCHING_ITEM}" ]; then
                    DELIVERY_ID=$(echo "${MATCHING_ITEM}" | jq -r .id 2>/dev/null || echo "")
                    DELIVERY_STATUS=$(echo "${MATCHING_ITEM}" | jq -r .status 2>/dev/null || echo "Unknown")
                    LAST_SEEN_STATUS="${DELIVERY_STATUS}"
                    echo "  Found delivery record ${DELIVERY_ID} with status '${DELIVERY_STATUS}' (poll ${i}/6)"
                    
                    if [ "${DELIVERY_STATUS}" = "Delivered" ]; then
                        DELIVERY_SUCCESS=true
                        break
                    fi
                fi
            fi
            echo "  Waiting for background worker dispatch and delivery... (${i}/6)"
        done

        if [ "${DELIVERY_SUCCESS}" = true ]; then
            record_result "${TEST_5_NAME}" "PASS"
        else
            record_result "${TEST_5_NAME}" "FAIL" "Delivery did not reach 'Delivered' state within timeout (last status: '${LAST_SEEN_STATUS}')"
        fi
    else
        record_result "${TEST_5_NAME}" "FAIL" "Failed to ingest matching event (HTTP ${HTTP_CODE})"
    fi
else
    record_result "${TEST_5_NAME}" "FAIL" "Prerequisite Test 4 failed; no endpoint available for dispatch test"
fi

# ------------------------------------------------------------------------------
# Test 6: Event Query Verification (GET /v1/events/{id})
# ------------------------------------------------------------------------------
echo "[TEST 6] Querying Event Status via GET /v1/events/{id}..."
TEST_6_NAME="Single Event Query (GET /v1/events/{id})"
TARGET_QUERY_ID="${EVENT_ID_1:-${EVENT_ID_2:-}}"
if [ -n "${TARGET_QUERY_ID}" ]; then
    if http_request GET "${API_BASE_URL}/v1/events/${TARGET_QUERY_ID}" true && [ "${HTTP_CODE}" = "200" ]; then
        if ! echo "${RESP_BODY}" | jq -e . >/dev/null 2>&1; then
            record_result "${TEST_6_NAME}" "FAIL" "Response is not valid JSON"
        else
            RETRIEVED_ID=$(echo "${RESP_BODY}" | jq -r .id 2>/dev/null || echo "")
            RETRIEVED_STATUS=$(echo "${RESP_BODY}" | jq -r .status 2>/dev/null || echo "")
            if [ "${RETRIEVED_ID}" != "${TARGET_QUERY_ID}" ]; then
                record_result "${TEST_6_NAME}" "FAIL" "Requested Event ID (${TARGET_QUERY_ID}) != Retrieved Event ID (${RETRIEVED_ID})"
            elif [ -z "${RETRIEVED_STATUS}" ] || [ "${RETRIEVED_STATUS}" = "null" ]; then
                record_result "${TEST_6_NAME}" "FAIL" "Required 'status' field is missing or null in event response"
            else
                echo "  Retrieved Event ID: ${RETRIEVED_ID}, Status: ${RETRIEVED_STATUS}"
                record_result "${TEST_6_NAME}" "PASS"
            fi
        fi
    else
        record_result "${TEST_6_NAME}" "FAIL" "Expected HTTP 200, got HTTP ${HTTP_CODE}"
    fi
else
    record_result "${TEST_6_NAME}" "FAIL" "No EventId available to query"
fi

# ------------------------------------------------------------------------------
# Test 7: Dead-Letter Replay Verification (Explicitly Optional Scenario)
# ------------------------------------------------------------------------------
echo "[TEST 7] Evaluating Dead-Letter Replay Eligibility..."
TEST_7_NAME="Manual Dead-Letter Replay & Replay Idempotency"
if http_request GET "${API_BASE_URL}/v1/deliveries?status=DeadLettered" true && [ "${HTTP_CODE}" = "200" ]; then
    DEAD_LETTER_COUNT=$(echo "${RESP_BODY}" | jq '.items | length' 2>/dev/null || echo "0")
    if [ "${DEAD_LETTER_COUNT}" -ge 1 ]; then
        TARGET_DL_ID=$(echo "${RESP_BODY}" | jq -r '.items[0].id')
        echo "  Found eligible DeadLettered delivery: ${TARGET_DL_ID}"
        REPLAY_IDEM_KEY="smoke-replay-$(date +%s)-$RANDOM"

        # First replay invocation
        if http_request POST "${API_BASE_URL}/v1/deliveries/${TARGET_DL_ID}/replay" true \
            -H "Idempotency-Key: ${REPLAY_IDEM_KEY}" \
            -H "Content-Type: application/json" && [ "${HTTP_CODE}" = "202" ]; then
            
            AUDIT_ID_1=$(echo "${RESP_BODY}" | jq -r .auditId 2>/dev/null || echo "")
            REPLAY_COUNT_1=$(echo "${RESP_BODY}" | jq -r .replayCount 2>/dev/null || echo "")

            # Second replay invocation with identical key to verify idempotency
            if http_request POST "${API_BASE_URL}/v1/deliveries/${TARGET_DL_ID}/replay" true \
                -H "Idempotency-Key: ${REPLAY_IDEM_KEY}" \
                -H "Content-Type: application/json" && [ "${HTTP_CODE}" = "202" ]; then
                
                AUDIT_ID_2=$(echo "${RESP_BODY}" | jq -r .auditId 2>/dev/null || echo "")
                REPLAY_COUNT_2=$(echo "${RESP_BODY}" | jq -r .replayCount 2>/dev/null || echo "")

                if [ -n "${AUDIT_ID_1}" ] && [ "${AUDIT_ID_1}" = "${AUDIT_ID_2}" ] && [ "${REPLAY_COUNT_1}" = "${REPLAY_COUNT_2}" ]; then
                    echo "  Replay Idempotency Confirmed: auditId=${AUDIT_ID_1}, replayCount=${REPLAY_COUNT_1}"
                    record_result "${TEST_7_NAME}" "PASS"
                else
                    record_result "${TEST_7_NAME}" "FAIL" "Replay idempotency mismatch: auditId1=${AUDIT_ID_1}, auditId2=${AUDIT_ID_2}"
                fi
            else
                record_result "${TEST_7_NAME}" "FAIL" "Duplicate replay request returned HTTP ${HTTP_CODE}"
            fi
        else
            record_result "${TEST_7_NAME}" "FAIL" "Initial replay request returned HTTP ${HTTP_CODE}"
        fi
    else
        record_result "${TEST_7_NAME}" "SKIPPED" "No DeadLettered deliveries found in environment; prerequisite not met"
    fi
else
    record_result "${TEST_7_NAME}" "FAIL" "Failed to query deliveries for DeadLettered records (HTTP ${HTTP_CODE})"
fi

# ==============================================================================
# Final Acceptance Summary
# ==============================================================================
echo "=============================================================================="
echo "SMOKE ACCEPTANCE SUITE SUMMARY"
echo "=============================================================================="
for line in "${TEST_RESULTS[@]}"; do
    echo "$line"
done
echo "------------------------------------------------------------------------------"
echo "Total Tests:   ${TESTS_TOTAL}"
echo "Passed:        ${TESTS_PASSED}"
echo "Skipped:       ${TESTS_SKIPPED}"
echo "Failed:        ${TESTS_FAILED}"
echo "=============================================================================="

if [ "${TESTS_FAILED}" -gt 0 ]; then
    echo "ERROR: One or more mandatory acceptance tests failed."
    exit 1
else
    if [ "${TESTS_SKIPPED}" -gt 0 ]; then
        echo "ALL MANDATORY SMOKE TESTS COMPLETED SUCCESSFULLY (${TESTS_SKIPPED} optional scenario skipped)!"
    else
        echo "ALL ACCEPTANCE TESTS COMPLETED SUCCESSFULLY!"
    fi
    exit 0
fi
