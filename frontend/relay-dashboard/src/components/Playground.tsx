import React, { useState } from 'react';
import { apiClient, ApiError } from '../api/client';
import type { IngestEventResponse, ProblemDetails } from '../types/api';
import { Play, KeyRound, RefreshCw, CheckCircle, AlertTriangle, XCircle, Clock, Copy, Check } from 'lucide-react';

interface PlaygroundProps {
  apiKey: string;
  setApiKey: (key: string) => void;
  onEventCreated?: (eventId: string) => void;
}

export const Playground: React.FC<PlaygroundProps> = ({ apiKey, setApiKey, onEventCreated }) => {
  const [eventType, setEventType] = useState('order.created');
  const [idempotencyKey, setIdempotencyKey] = useState(() => `idem_${crypto.randomUUID()}`);
  const [payloadStr, setPayloadStr] = useState(() => JSON.stringify({
    orderId: 'ORD-1001',
    amount: 1499.00,
    currency: 'INR',
    items: [
      { id: 'SKU-89', quantity: 2, price: 499.50 },
      { id: 'SKU-12', quantity: 1, price: 500.00 }
    ],
    customer: {
      id: 'CUST-42',
      email: 'buyer@example.com'
    }
  }, null, 2));

  const [loading, setLoading] = useState(false);
  const [responseStatus, setResponseStatus] = useState<number | null>(null);
  const [responseHeaders, setResponseHeaders] = useState<Record<string, string>>({});
  const [responseData, setResponseData] = useState<IngestEventResponse | ProblemDetails | null>(null);
  const [duration, setDuration] = useState<number | null>(null);
  const [copied, setCopied] = useState(false);

  const generateNewIdempotencyKey = () => {
    setIdempotencyKey(`idem_${crypto.randomUUID()}`);
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!apiKey.trim()) {
      alert('Please enter an API key to submit requests.');
      return;
    }

    let parsedPayload: Record<string, unknown>;
    try {
      parsedPayload = JSON.parse(payloadStr);
    } catch {
      alert('Payload is not valid JSON. Please check JSON syntax.');
      return;
    }

    setLoading(true);
    setResponseStatus(null);
    setResponseData(null);
    setResponseHeaders({});
    setDuration(null);

    try {
      const res = await apiClient.ingestEvent(apiKey.trim(), idempotencyKey.trim(), {
        eventType: eventType.trim(),
        payload: parsedPayload
      });

      setResponseStatus(res.status);
      setResponseData(res.response);
      setDuration(res.durationMs);
      if (res.location) {
        setResponseHeaders({ Location: res.location });
      }

      if (onEventCreated) {
        onEventCreated(res.response.eventId);
      }
    } catch (err) {
      if (err instanceof ApiError) {
        setResponseStatus(err.status);
        setResponseData(err.problem);
      } else {
        setResponseStatus(500);
        setResponseData({
          status: 500,
          title: 'Client Network Error',
          detail: (err as Error).message
        });
      }
    } finally {
      setLoading(false);
    }
  };

  const copyToClipboard = (text: string) => {
    navigator.clipboard.writeText(text);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  return (
    <div style={{ maxWidth: '1200px', margin: '0 auto', display: 'flex', flexDirection: 'column', gap: '24px' }}>
      <div>
        <h2 style={{ fontSize: '24px', fontWeight: 700, letterSpacing: '-0.02em', marginBottom: '6px' }}>
          Event Playground
        </h2>
        <p style={{ color: 'var(--text-secondary)', fontSize: '14px' }}>
          Test event ingestion, idempotency conflict detection, and transactional persistence in real time.
        </p>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '24px' }}>
        {/* Request Form */}
        <form onSubmit={handleSubmit} style={{
          backgroundColor: 'var(--bg-secondary)',
          borderRadius: '10px',
          border: '1px solid var(--border)',
          padding: '24px',
          display: 'flex',
          flexDirection: 'column',
          gap: '18px'
        }}>
          <h3 style={{ fontSize: '15px', fontWeight: 600, display: 'flex', alignItems: 'center', gap: '8px' }}>
            <Play size={16} color="var(--accent-cyan)" />
            HTTP Request (POST /v1/events)
          </h3>

          {/* API Key */}
          <div>
            <label style={{ display: 'block', fontSize: '13px', fontWeight: 500, color: 'var(--text-secondary)', marginBottom: '6px' }}>
              API Key (X-Api-Key)
            </label>
            <div style={{ position: 'relative' }}>
              <input
                type="text"
                placeholder="rl_live_pk_dev001_..."
                value={apiKey}
                onChange={e => setApiKey(e.target.value)}
                required
                className="font-mono"
                style={{
                  width: '100%',
                  padding: '9px 12px 9px 36px',
                  backgroundColor: 'var(--bg-card)',
                  border: '1px solid var(--border)',
                  borderRadius: '6px',
                  color: 'var(--text-primary)',
                  fontSize: '13px'
                }}
              />
              <KeyRound size={16} style={{ position: 'absolute', left: '12px', top: '11px', color: 'var(--text-muted)' }} />
            </div>
            <span style={{ fontSize: '11px', color: 'var(--text-muted)' }}>
              Ephemeral React state only. Never persisted in local storage or logged.
            </span>
          </div>

          {/* Event Type */}
          <div>
            <label style={{ display: 'block', fontSize: '13px', fontWeight: 500, color: 'var(--text-secondary)', marginBottom: '6px' }}>
              Event Type
            </label>
            <input
              type="text"
              value={eventType}
              onChange={e => setEventType(e.target.value)}
              placeholder="e.g. order.created"
              required
              className="font-mono"
              style={{
                width: '100%',
                padding: '9px 12px',
                backgroundColor: 'var(--bg-card)',
                border: '1px solid var(--border)',
                borderRadius: '6px',
                color: 'var(--text-primary)',
                fontSize: '13px'
              }}
            />
          </div>

          {/* Idempotency Key */}
          <div>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '6px' }}>
              <label style={{ fontSize: '13px', fontWeight: 500, color: 'var(--text-secondary)' }}>
                Idempotency-Key
              </label>
              <button
                type="button"
                onClick={generateNewIdempotencyKey}
                style={{
                  background: 'none',
                  border: 'none',
                  color: 'var(--accent-cyan)',
                  fontSize: '12px',
                  cursor: 'pointer',
                  display: 'flex',
                  alignItems: 'center',
                  gap: '4px'
                }}
              >
                <RefreshCw size={12} />
                Generate New Key
              </button>
            </div>
            <input
              type="text"
              value={idempotencyKey}
              onChange={e => setIdempotencyKey(e.target.value)}
              required
              className="font-mono"
              style={{
                width: '100%',
                padding: '9px 12px',
                backgroundColor: 'var(--bg-card)',
                border: '1px solid var(--border)',
                borderRadius: '6px',
                color: 'var(--text-primary)',
                fontSize: '13px'
              }}
            />
            <span style={{ fontSize: '11px', color: 'var(--text-muted)' }}>
              Tip: Re-submit with identical payload to observe 202 replay, or alter payload to test 409 Conflict.
            </span>
          </div>

          {/* JSON Payload Editor */}
          <div>
            <label style={{ display: 'block', fontSize: '13px', fontWeight: 500, color: 'var(--text-secondary)', marginBottom: '6px' }}>
              JSON Payload (Max 32 KB)
            </label>
            <textarea
              rows={9}
              value={payloadStr}
              onChange={e => setPayloadStr(e.target.value)}
              required
              className="font-mono"
              style={{
                width: '100%',
                padding: '10px 12px',
                backgroundColor: 'var(--bg-card)',
                border: '1px solid var(--border)',
                borderRadius: '6px',
                color: 'var(--text-primary)',
                fontSize: '12px',
                lineHeight: 1.4,
                resize: 'vertical'
              }}
            />
          </div>

          {/* Submit */}
          <button
            type="submit"
            disabled={loading}
            style={{
              padding: '12px 18px',
              backgroundColor: 'var(--accent-cyan)',
              color: '#090d16',
              fontWeight: 600,
              border: 'none',
              borderRadius: '6px',
              cursor: loading ? 'not-allowed' : 'pointer',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              gap: '8px',
              fontSize: '14px',
              transition: 'opacity 0.15s ease',
              opacity: loading ? 0.7 : 1
            }}
          >
            {loading ? <RefreshCw size={16} className="animate-spin" /> : <Play size={16} />}
            {loading ? 'Ingesting Event...' : 'Submit Event'}
          </button>
        </form>

        {/* Response Panel */}
        <div style={{
          backgroundColor: 'var(--bg-secondary)',
          borderRadius: '10px',
          border: '1px solid var(--border)',
          padding: '24px',
          display: 'flex',
          flexDirection: 'column',
          gap: '16px'
        }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            <h3 style={{ fontSize: '15px', fontWeight: 600 }}>Response Inspection</h3>
            {duration !== null && (
              <span style={{ fontSize: '12px', color: 'var(--text-muted)', display: 'flex', alignItems: 'center', gap: '4px' }}>
                <Clock size={12} />
                {duration} ms
              </span>
            )}
          </div>

          {responseStatus === null ? (
            <div style={{
              flex: 1,
              display: 'flex',
              flexDirection: 'column',
              alignItems: 'center',
              justifyContent: 'center',
              color: 'var(--text-muted)',
              fontSize: '14px',
              border: '1px dashed var(--border)',
              borderRadius: '8px',
              padding: '40px 20px',
              textAlign: 'center'
            }}>
              <Play size={32} style={{ marginBottom: '12px', opacity: 0.4 }} />
              Submit an event to inspect HTTP status codes, headers, and transactional response payloads.
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '14px', flex: 1 }}>
              {/* Status Header Badge */}
              <div style={{
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'space-between',
                padding: '12px 16px',
                borderRadius: '8px',
                backgroundColor:
                  responseStatus === 202 ? 'rgba(16, 185, 129, 0.1)' :
                  responseStatus === 409 ? 'rgba(245, 158, 11, 0.1)' :
                  'rgba(239, 68, 68, 0.1)',
                border: `1px solid ${
                  responseStatus === 202 ? 'rgba(16, 185, 129, 0.3)' :
                  responseStatus === 409 ? 'rgba(245, 158, 11, 0.3)' :
                  'rgba(239, 68, 68, 0.3)'
                }`
              }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                  {responseStatus === 202 && <CheckCircle size={18} color="var(--accent-green)" />}
                  {responseStatus === 409 && <AlertTriangle size={18} color="var(--accent-amber)" />}
                  {responseStatus >= 400 && responseStatus !== 409 && <XCircle size={18} color="var(--accent-red)" />}
                  <span className="font-mono" style={{ fontWeight: 700, fontSize: '15px' }}>
                    HTTP {responseStatus} {responseStatus === 202 ? 'Accepted' : responseStatus === 409 ? 'Conflict' : 'Error'}
                  </span>
                </div>
                <span style={{ fontSize: '12px', color: 'var(--text-secondary)' }}>
                  {responseStatus === 202 ? 'Event & Outbox Durably Stored' :
                   responseStatus === 409 ? 'Idempotency Conflict Detected' : 'Request Rejected'}
                </span>
              </div>

              {/* Response Headers */}
              {Object.keys(responseHeaders).length > 0 && (
                <div style={{
                  padding: '10px 14px',
                  borderRadius: '6px',
                  backgroundColor: 'var(--bg-card)',
                  fontSize: '12px'
                }}>
                  <div style={{ fontWeight: 600, color: 'var(--text-secondary)', marginBottom: '4px' }}>Headers:</div>
                  {Object.entries(responseHeaders).map(([k, v]) => (
                    <div key={k} className="font-mono" style={{ color: 'var(--accent-cyan)' }}>
                      {k}: {v}
                    </div>
                  ))}
                </div>
              )}

              {/* JSON Body View */}
              <div style={{ position: 'relative', flex: 1 }}>
                <div style={{
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'center',
                  marginBottom: '6px'
                }}>
                  <span style={{ fontSize: '12px', color: 'var(--text-muted)' }}>Response Body:</span>
                  <button
                    onClick={() => copyToClipboard(JSON.stringify(responseData, null, 2))}
                    style={{
                      background: 'none',
                      border: 'none',
                      color: 'var(--text-muted)',
                      cursor: 'pointer',
                      display: 'flex',
                      alignItems: 'center',
                      gap: '4px',
                      fontSize: '11px'
                    }}
                  >
                    {copied ? <Check size={12} color="var(--accent-green)" /> : <Copy size={12} />}
                    {copied ? 'Copied' : 'Copy JSON'}
                  </button>
                </div>
                <pre className="font-mono" style={{
                  backgroundColor: 'var(--bg-primary)',
                  padding: '14px',
                  borderRadius: '6px',
                  fontSize: '12px',
                  overflowX: 'auto',
                  border: '1px solid var(--border)',
                  color: '#e2e8f0',
                  maxHeight: '320px'
                }}>
                  {JSON.stringify(responseData, null, 2)}
                </pre>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
};
