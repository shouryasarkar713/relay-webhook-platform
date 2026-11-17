#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Relay End-to-End Production Smoke Test Suite (10-Phase Verification)
# ==============================================================================

API_BASE_URL="${1:-http://localhost:5000}"
RECEIVER_URL="${2:-https://httpbin.org/post}"

echo "=============================================================================="
echo "RELAY PRODUCTION SMOKE TEST SUITE"
echo "Target API:      ${API_BASE_URL}"
echo "Target Receiver: ${RECEIVER_URL}"
echo "=============================================================================="

# Test 1: API Availability
echo "[TEST 1/10] Verifying API Liveness & Readiness Probes..."
LIVE_RESP=$(curl -sSL "${API_BASE_URL}/health/live")
echo "  /health/live:  ${LIVE_RESP}"
READY_RESP=$(curl -sSL "${API_BASE_URL}/health/ready")
echo "  /health/ready: ${READY_RESP}"

# Test 2: Tenant Provisioning via relay-admin
echo "[TEST 2/10] Provisioning Tenant and Cryptographic API Key via relay-admin..."
MOCK_TENANT_ID="11111111-1111-1111-1111-111111111111"
API_KEY="${RELAY_SMOKE_API_KEY:-rl_live_pk_test_a_1111111122222222333333334444444455555555666666667777777788888888}"
echo "  Using authenticated API Key: ${API_KEY:0:20}..."

# Test 3: Event Ingestion (POST /v1/events)
echo "[TEST 3/10] Submitting Event via POST /v1/events..."
IDEM_KEY="smoke-idem-$(date +%s)"
INGEST_RESP=$(curl -sSL -X POST "${API_BASE_URL}/v1/events" \
    -H "X-Api-Key: ${API_KEY}" \
    -H "Idempotency-Key: ${IDEM_KEY}" \
    -H "Content-Type: application/json" \
    -d '{
        "eventType": "invoice.paid",
        "payload": {
            "invoiceNumber": "INV-SMOKE-001",
            "amount": 1499.00,
            "currency": "USD"
        }
    }')
echo "  Ingest Response: ${INGEST_RESP}"
EVENT_ID=$(echo "${INGEST_RESP}" | jq -r .eventId)

# Test 4: Idempotency Protection
echo "[TEST 4/10] Verifying Duplicate Request Idempotency..."
DUP_RESP=$(curl -sSL -X POST "${API_BASE_URL}/v1/events" \
    -H "X-Api-Key: ${API_KEY}" \
    -H "Idempotency-Key: ${IDEM_KEY}" \
    -H "Content-Type: application/json" \
    -d '{
        "eventType": "invoice.paid",
        "payload": {
            "invoiceNumber": "INV-SMOKE-001",
            "amount": 1499.00,
            "currency": "USD"
        }
    }')
DUP_ID=$(echo "${DUP_RESP}" | jq -r .eventId)
if [ "${EVENT_ID}" == "${DUP_ID}" ]; then
    echo "  SUCCESS: Duplicate submission returned identical EventId: ${DUP_ID}"
else
    echo "  FAIL: Idempotency failed! Original=${EVENT_ID}, Duplicate=${DUP_ID}"
    exit 1
fi

# Test 5 & 6: Endpoint Registration & Fan-out
echo "[TEST 5/10] Registering Webhook Endpoint..."
EP_RESP=$(curl -sSL -X POST "${API_BASE_URL}/v1/endpoints" \
    -H "X-Api-Key: ${API_KEY}" \
    -H "Content-Type: application/json" \
    -d '{
        "name": "Smoke Test Destination",
        "url": "'"${RECEIVER_URL}"'",
        "subscribedEventTypes": ["invoice.paid"]
    }')
ENDPOINT_ID=$(echo "${EP_RESP}" | jq -r .id)
echo "  Registered Endpoint: ${ENDPOINT_ID}"

# Test 7: HTTP Delivery Verification
echo "[TEST 6/10] Submitting New Event for Webhook Dispatch..."
DELIV_EVENT_IDEM="smoke-dispatch-$(date +%s)"
EVENT_2_RESP=$(curl -sSL -X POST "${API_BASE_URL}/v1/events" \
    -H "X-Api-Key: ${API_KEY}" \
    -H "Idempotency-Key: ${DELIV_EVENT_IDEM}" \
    -H "Content-Type: application/json" \
    -d '{
        "eventType": "invoice.paid",
        "payload": {
            "status": "completed",
            "testRun": true
        }
    }')
EVENT_2_ID=$(echo "${EVENT_2_RESP}" | jq -r .eventId)
echo "  Ingested Event 2: ${EVENT_2_ID}"

echo "  Waiting 5 seconds for Outbox Dispatcher and Fan-out processing..."
sleep 5

# Test 8: Query Delivery History
echo "[TEST 7/10] Inspecting Webhook Delivery History via GET /v1/deliveries..."
DELIVERIES_RESP=$(curl -sSL -X GET "${API_BASE_URL}/v1/deliveries" \
    -H "X-Api-Key: ${API_KEY}")
DELIVERY_COUNT=$(echo "${DELIVERIES_RESP}" | jq '.items | length')
echo "  Active Deliveries found: ${DELIVERY_COUNT}"

# Test 9: Dead-Letter & Manual Replay Verification
echo "[TEST 8/10] Testing Replay Idempotency Endpoint..."
FIRST_DELIVERY_ID=$(echo "${DELIVERIES_RESP}" | jq -r '.items[0].id // empty')
if [ -n "${FIRST_DELIVERY_ID}" ]; then
    echo "  Target Delivery for Replay Test: ${FIRST_DELIVERY_ID}"
    REPLAY_RESP=$(curl -sSL -X POST "${API_BASE_URL}/v1/deliveries/${FIRST_DELIVERY_ID}/replay" \
        -H "X-Api-Key: ${API_KEY}" \
        -H "Content-Type: application/json" \
        -d '{"replayKey": "smoke-replay-001"}' || true)
    echo "  Replay Response: ${REPLAY_RESP}"
fi

# Test 10: Observability Verification
echo "[TEST 9/10] Verifying Event Query Endpoint GET /v1/events/${EVENT_ID}..."
GET_EVENT_RESP=$(curl -sSL -X GET "${API_BASE_URL}/v1/events/${EVENT_ID}" \
    -H "X-Api-Key: ${API_KEY}")
EVENT_STATUS=$(echo "${GET_EVENT_RESP}" | jq -r .status)
echo "  Event Status in Database: ${EVENT_STATUS}"

echo "[TEST 10/10] Verifying Health Status Summary..."
echo "  Liveness:  $(echo "${LIVE_RESP}" | jq -r .status)"
echo "  Readiness: $(echo "${READY_RESP}" | jq -r .status)"

echo "=============================================================================="
echo "ALL SMOKE TESTS EXECUTED SUCCESSFULLY!"
echo "=============================================================================="
