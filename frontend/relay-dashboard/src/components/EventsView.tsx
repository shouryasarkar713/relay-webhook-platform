import React, { useEffect, useState } from 'react';
import { apiClient, ApiError } from '../api/client';
import type { EventDto } from '../types/api';
import { RefreshCw, Radio, KeyRound, Eye, Hash, Calendar, ShieldAlert } from 'lucide-react';

interface EventsViewProps {
  apiKey: string;
  setApiKey: (key: string) => void;
  selectedEventId?: string | null;
}

export const EventsView: React.FC<EventsViewProps> = ({ apiKey, setApiKey, selectedEventId }) => {
  const [events, setEvents] = useState<EventDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [inspectedEvent, setInspectedEvent] = useState<EventDto | null>(null);

  const fetchEvents = async () => {
    if (!apiKey.trim()) {
      return;
    }

    setLoading(true);
    setError(null);

    try {
      const result = await apiClient.listEvents(apiKey.trim(), 50);
      setEvents(result.items);

      if (selectedEventId) {
        const found = result.items.find(e => e.id === selectedEventId);
        if (found) {
          setInspectedEvent(found);
        }
      }
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.problem.detail || err.problem.title);
      } else {
        setError((err as Error).message);
      }
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (apiKey.trim()) {
      fetchEvents();
    }
  }, [apiKey]);

  return (
    <div style={{ maxWidth: '1200px', margin: '0 auto', display: 'flex', flexDirection: 'column', gap: '20px' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div>
          <h2 style={{ fontSize: '24px', fontWeight: 700, letterSpacing: '-0.02em', marginBottom: '4px' }}>
            Ingested Events
          </h2>
          <p style={{ color: 'var(--text-secondary)', fontSize: '14px' }}>
            Durably stored events scoped strictly to your authenticated tenant.
          </p>
        </div>

        <button
          onClick={fetchEvents}
          disabled={loading || !apiKey.trim()}
          style={{
            padding: '8px 14px',
            backgroundColor: 'var(--bg-card)',
            border: '1px solid var(--border)',
            color: 'var(--text-primary)',
            borderRadius: '6px',
            cursor: loading || !apiKey.trim() ? 'not-allowed' : 'pointer',
            display: 'flex',
            alignItems: 'center',
            gap: '6px',
            fontSize: '13px',
            fontWeight: 500
          }}
        >
          <RefreshCw size={14} className={loading ? 'animate-spin' : ''} />
          Refresh
        </button>
      </div>

      {!apiKey.trim() ? (
        <div style={{
          backgroundColor: 'var(--bg-secondary)',
          borderRadius: '10px',
          border: '1px solid var(--border)',
          padding: '40px 20px',
          textAlign: 'center',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          gap: '14px'
        }}>
          <KeyRound size={36} color="var(--accent-amber)" />
          <h3 style={{ fontSize: '16px', fontWeight: 600 }}>API Key Required</h3>
          <p style={{ color: 'var(--text-secondary)', fontSize: '14px', maxWidth: '420px' }}>
            Enter your active tenant API key to authenticate and query events from SQL Server.
          </p>
          <div style={{ display: 'flex', gap: '8px', maxWidth: '400px', width: '100%' }}>
            <input
              type="text"
              placeholder="rl_live_pk_dev001_..."
              className="font-mono"
              onChange={e => setApiKey(e.target.value)}
              style={{
                flex: 1,
                padding: '9px 12px',
                backgroundColor: 'var(--bg-card)',
                border: '1px solid var(--border)',
                borderRadius: '6px',
                color: 'var(--text-primary)',
                fontSize: '13px'
              }}
            />
          </div>
        </div>
      ) : error ? (
        <div style={{
          backgroundColor: 'rgba(239, 68, 68, 0.1)',
          border: '1px solid rgba(239, 68, 68, 0.25)',
          borderRadius: '8px',
          padding: '16px',
          color: 'var(--accent-red)',
          display: 'flex',
          alignItems: 'center',
          gap: '10px',
          fontSize: '14px'
        }}>
          <ShieldAlert size={20} />
          <span>Error loading events: {error}</span>
        </div>
      ) : (
        <div style={{ display: 'grid', gridTemplateColumns: inspectedEvent ? '1fr 420px' : '1fr', gap: '20px' }}>
          {/* Events Table */}
          <div style={{
            backgroundColor: 'var(--bg-secondary)',
            borderRadius: '10px',
            border: '1px solid var(--border)',
            overflow: 'hidden'
          }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', textAlign: 'left', fontSize: '13px' }}>
              <thead>
                <tr style={{
                  borderBottom: '1px solid var(--border)',
                  backgroundColor: 'rgba(15, 23, 42, 0.6)',
                  color: 'var(--text-secondary)'
                }}>
                  <th style={{ padding: '12px 16px', fontWeight: 600 }}>Event ID</th>
                  <th style={{ padding: '12px 16px', fontWeight: 600 }}>Event Type</th>
                  <th style={{ padding: '12px 16px', fontWeight: 600 }}>Created (UTC)</th>
                  <th style={{ padding: '12px 16px', fontWeight: 600 }}>Status</th>
                  <th style={{ padding: '12px 16px', fontWeight: 600, textAlign: 'right' }}>Action</th>
                </tr>
              </thead>
              <tbody>
                {events.length === 0 ? (
                  <tr>
                    <td colSpan={5} style={{ padding: '40px', textAlign: 'center', color: 'var(--text-muted)' }}>
                      No events found for this tenant. Use the Event Playground to submit one!
                    </td>
                  </tr>
                ) : (
                  events.map(e => (
                    <tr
                      key={e.id}
                      onClick={() => setInspectedEvent(e)}
                      style={{
                        borderBottom: '1px solid var(--border)',
                        cursor: 'pointer',
                        backgroundColor: inspectedEvent?.id === e.id ? 'var(--bg-card)' : 'transparent',
                        transition: 'background-color 0.15s ease'
                      }}
                    >
                      <td style={{ padding: '12px 16px' }}>
                        <span className="font-mono" style={{ color: 'var(--accent-cyan)' }}>
                          {e.id.slice(0, 8)}...{e.id.slice(-4)}
                        </span>
                      </td>
                      <td style={{ padding: '12px 16px', fontWeight: 500 }}>
                        <span style={{
                          backgroundColor: 'rgba(56, 189, 248, 0.1)',
                          color: 'var(--accent-cyan)',
                          padding: '3px 8px',
                          borderRadius: '4px',
                          fontSize: '12px'
                        }}>
                          {e.eventType}
                        </span>
                      </td>
                      <td style={{ padding: '12px 16px', color: 'var(--text-secondary)' }}>
                        {new Date(e.createdAtUtc).toLocaleString('en-US', { timeZone: 'UTC' })} UTC
                      </td>
                      <td style={{ padding: '12px 16px' }}>
                        <span style={{
                          backgroundColor: 'rgba(16, 185, 129, 0.1)',
                          color: 'var(--accent-green)',
                          padding: '3px 8px',
                          borderRadius: '4px',
                          fontSize: '11px',
                          fontWeight: 600,
                          textTransform: 'uppercase'
                        }}>
                          {e.status}
                        </span>
                      </td>
                      <td style={{ padding: '12px 16px', textAlign: 'right' }}>
                        <button
                          onClick={(evt) => {
                            evt.stopPropagation();
                            setInspectedEvent(e);
                          }}
                          style={{
                            background: 'none',
                            border: 'none',
                            color: 'var(--text-secondary)',
                            cursor: 'pointer'
                          }}
                          title="Inspect details"
                        >
                          <Eye size={16} />
                        </button>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>

          {/* Event Detail Drawer */}
          {inspectedEvent && (
            <div style={{
              backgroundColor: 'var(--bg-secondary)',
              borderRadius: '10px',
              border: '1px solid var(--border)',
              padding: '20px',
              display: 'flex',
              flexDirection: 'column',
              gap: '16px'
            }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <h3 style={{ fontSize: '15px', fontWeight: 600, display: 'flex', alignItems: 'center', gap: '8px' }}>
                  <Radio size={16} color="var(--accent-cyan)" />
                  Event Inspector
                </h3>
                <button
                  onClick={() => setInspectedEvent(null)}
                  style={{
                    background: 'none',
                    border: 'none',
                    color: 'var(--text-muted)',
                    cursor: 'pointer',
                    fontSize: '16px'
                  }}
                >
                  ✕
                </button>
              </div>

              <div style={{ display: 'flex', flexDirection: 'column', gap: '10px', fontSize: '12px' }}>
                <div>
                  <div style={{ color: 'var(--text-muted)', marginBottom: '2px' }}>Event ID:</div>
                  <div className="font-mono" style={{ color: 'var(--accent-cyan)', wordBreak: 'break-all' }}>
                    {inspectedEvent.id}
                  </div>
                </div>

                <div>
                  <div style={{ color: 'var(--text-muted)', marginBottom: '2px' }}>Tenant ID:</div>
                  <div className="font-mono" style={{ color: 'var(--text-secondary)', wordBreak: 'break-all' }}>
                    {inspectedEvent.tenantId}
                  </div>
                </div>

                <div>
                  <div style={{ color: 'var(--text-muted)', marginBottom: '2px', display: 'flex', alignItems: 'center', gap: '4px' }}>
                    <Hash size={12} />
                    Idempotency Key:
                  </div>
                  <div className="font-mono" style={{ color: 'var(--text-primary)', wordBreak: 'break-all' }}>
                    {inspectedEvent.idempotencyKey}
                  </div>
                </div>

                <div>
                  <div style={{ color: 'var(--text-muted)', marginBottom: '2px', display: 'flex', alignItems: 'center', gap: '4px' }}>
                    <Hash size={12} />
                    Payload SHA-256 Hash:
                  </div>
                  <div className="font-mono" style={{ color: '#94a3b8', fontSize: '11px', wordBreak: 'break-all' }}>
                    {inspectedEvent.payloadHash}
                  </div>
                </div>

                <div>
                  <div style={{ color: 'var(--text-muted)', marginBottom: '2px', display: 'flex', alignItems: 'center', gap: '4px' }}>
                    <Calendar size={12} />
                    Created At (UTC):
                  </div>
                  <div style={{ color: 'var(--text-primary)' }}>
                    {new Date(inspectedEvent.createdAtUtc).toISOString()}
                  </div>
                </div>

                <div>
                  <div style={{ color: 'var(--text-muted)', marginBottom: '4px' }}>Payload JSON:</div>
                  <pre className="font-mono" style={{
                    backgroundColor: 'var(--bg-primary)',
                    padding: '10px',
                    borderRadius: '6px',
                    border: '1px solid var(--border)',
                    fontSize: '11px',
                    overflowX: 'auto',
                    maxHeight: '240px'
                  }}>
                    {(() => {
                      try {
                        return JSON.stringify(JSON.parse(inspectedEvent.payloadJson), null, 2);
                      } catch {
                        return inspectedEvent.payloadJson;
                      }
                    })()}
                  </pre>
                </div>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
};
