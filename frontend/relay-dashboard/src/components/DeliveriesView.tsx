import React, { useEffect, useState } from 'react';
import { apiClient, ApiError } from '../api/client';
import type { WebhookDeliveryDto } from '../types/api';
import {
  RefreshCw,
  KeyRound,
  Eye,
  CheckCircle2,
  XCircle,
  Clock,
  AlertTriangle,
  MinusCircle,
  RotateCcw
} from 'lucide-react';

interface DeliveriesViewProps {
  apiKey: string;
  setApiKey: (key: string) => void;
}

export const DeliveriesView: React.FC<DeliveriesViewProps> = ({ apiKey, setApiKey }) => {
  const [deliveries, setDeliveries] = useState<WebhookDeliveryDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Filters
  const [statusFilter, setStatusFilter] = useState<string>('');
  const [eventTypeFilter, setEventTypeFilter] = useState<string>('');

  // Selected delivery for detail modal/drawer
  const [selectedDelivery, setSelectedDelivery] = useState<WebhookDeliveryDto | null>(null);

  // Replay modal state
  const [replayTarget, setReplayTarget] = useState<WebhookDeliveryDto | null>(null);
  const [replayKey, setReplayKey] = useState<string>('');
  const [replayLoading, setReplayLoading] = useState(false);
  const [replayError, setReplayError] = useState<string | null>(null);
  const [replaySuccess, setReplaySuccess] = useState<string | null>(null);

  const openReplayModal = (delivery: WebhookDeliveryDto) => {
    setReplayTarget(delivery);
    setReplayKey(`rep_${Date.now()}`);
    setReplayError(null);
    setReplaySuccess(null);
  };

  const handleReplaySubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!replayTarget || !apiKey.trim()) return;

    setReplayLoading(true);
    setReplayError(null);

    try {
      const res = await apiClient.replayDelivery(apiKey.trim(), replayTarget.id, replayKey.trim() || undefined);
      setReplaySuccess(`Delivery replayed successfully! Status reset to '${res.status}' (Replay #${res.replayCount}).`);
      await fetchDeliveries();
      setTimeout(() => {
        setReplayTarget(null);
        setReplaySuccess(null);
      }, 1500);
    } catch (err) {
      if (err instanceof ApiError) {
        setReplayError(err.problem.detail || err.problem.title);
      } else {
        setReplayError((err as Error).message);
      }
    } finally {
      setReplayLoading(false);
    }
  };

  const fetchDeliveries = async () => {
    if (!apiKey.trim()) return;

    setLoading(true);
    setError(null);

    try {
      const result = await apiClient.listDeliveries(apiKey.trim(), {
        status: statusFilter || undefined,
        eventType: eventTypeFilter.trim() || undefined,
        pageSize: 50
      });
      setDeliveries(result.items);

      if (selectedDelivery) {
        const updated = result.items.find(d => d.id === selectedDelivery.id);
        if (updated) setSelectedDelivery(updated);
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
      fetchDeliveries();
    }
  }, [apiKey, statusFilter]);

  const renderStatusBadge = (status: string, httpCode?: number | null) => {
    switch (status.toLowerCase()) {
      case 'delivered':
        return (
          <span style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: '4px',
            padding: '2px 8px',
            borderRadius: '12px',
            fontSize: '11px',
            fontWeight: 600,
            backgroundColor: 'rgba(34, 197, 94, 0.12)',
            color: '#4ade80'
          }}>
            <CheckCircle2 size={12} />
            Delivered {httpCode ? `(${httpCode})` : ''}
          </span>
        );
      case 'failed':
        return (
          <span style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: '4px',
            padding: '2px 8px',
            borderRadius: '12px',
            fontSize: '11px',
            fontWeight: 600,
            backgroundColor: 'rgba(239, 68, 68, 0.12)',
            color: '#f87171'
          }}>
            <XCircle size={12} />
            Failed {httpCode ? `(${httpCode})` : ''}
          </span>
        );
      case 'processing':
        return (
          <span style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: '4px',
            padding: '2px 8px',
            borderRadius: '12px',
            fontSize: '11px',
            fontWeight: 600,
            backgroundColor: 'rgba(56, 189, 248, 0.12)',
            color: 'var(--accent-cyan)'
          }}>
            <Clock size={12} />
            Processing
          </span>
        );
      case 'skipped':
        return (
          <span style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: '4px',
            padding: '2px 8px',
            borderRadius: '12px',
            fontSize: '11px',
            fontWeight: 600,
            backgroundColor: 'rgba(148, 163, 184, 0.12)',
            color: 'var(--text-muted)'
          }}>
            <MinusCircle size={12} />
            Skipped
          </span>
        );
      case 'deadlettered':
        return (
          <span style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: '4px',
            padding: '2px 8px',
            borderRadius: '12px',
            fontSize: '11px',
            fontWeight: 600,
            backgroundColor: 'rgba(244, 63, 94, 0.15)',
            color: '#fb7185',
            border: '1px solid rgba(244, 63, 94, 0.3)'
          }}>
            <AlertTriangle size={12} />
            Dead-Lettered {httpCode ? `(${httpCode})` : ''}
          </span>
        );
      default:
        return (
          <span style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: '4px',
            padding: '2px 8px',
            borderRadius: '12px',
            fontSize: '11px',
            fontWeight: 600,
            backgroundColor: 'rgba(234, 179, 8, 0.12)',
            color: '#fbbf24'
          }}>
            <Clock size={12} />
            Pending
          </span>
        );
    }
  };

  return (
    <div style={{ maxWidth: '1200px', margin: '0 auto', display: 'flex', flexDirection: 'column', gap: '20px' }}>
      {/* Header */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div>
          <h2 style={{ fontSize: '24px', fontWeight: 700, letterSpacing: '-0.02em', marginBottom: '4px' }}>
            Webhook Deliveries
          </h2>
          <p style={{ color: 'var(--text-secondary)', fontSize: '14px' }}>
            Inspect outbound HTTP POST deliveries, HTTP response status codes, and attempt logs.
          </p>
        </div>

        <button
          onClick={fetchDeliveries}
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
          <RefreshCw size={14} className={loading ? 'spin' : ''} />
          Refresh
        </button>
      </div>

      {/* No API Key */}
      {!apiKey.trim() && (
        <div style={{
          padding: '40px 20px',
          textAlign: 'center',
          backgroundColor: 'var(--bg-secondary)',
          borderRadius: '10px',
          border: '1px dashed var(--border)'
        }}>
          <KeyRound size={36} color="var(--accent-cyan)" style={{ marginBottom: '12px' }} />
          <h3 style={{ fontSize: '18px', fontWeight: 600, marginBottom: '8px' }}>Authentication Required</h3>
          <p style={{ color: 'var(--text-secondary)', fontSize: '14px', maxWidth: '460px', margin: '0 auto 20px' }}>
            Enter your tenant API key to inspect webhook deliveries.
          </p>
          <div style={{ display: 'flex', gap: '8px', maxWidth: '420px', margin: '0 auto' }}>
            <input
              type="password"
              placeholder="rl_live_..."
              onKeyDown={(e) => {
                if (e.key === 'Enter') setApiKey((e.target as HTMLInputElement).value);
              }}
              style={{
                flex: 1,
                padding: '8px 12px',
                borderRadius: '6px',
                border: '1px solid var(--border)',
                backgroundColor: 'var(--bg-primary)',
                color: 'var(--text-primary)',
                fontSize: '13px',
                fontFamily: 'monospace'
              }}
            />
            <button
              onClick={(e) => {
                const input = (e.currentTarget.previousSibling as HTMLInputElement);
                if (input) setApiKey(input.value);
              }}
              style={{
                padding: '8px 16px',
                backgroundColor: 'var(--accent-cyan)',
                color: '#090d16',
                borderRadius: '6px',
                border: 'none',
                fontWeight: 600,
                cursor: 'pointer'
              }}
            >
              Set Key
            </button>
          </div>
        </div>
      )}

      {/* Filters Bar */}
      {apiKey.trim() && (
        <div style={{
          display: 'flex',
          gap: '12px',
          alignItems: 'center',
          backgroundColor: 'var(--bg-secondary)',
          padding: '12px 16px',
          borderRadius: '8px',
          border: '1px solid var(--border)'
        }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
            <label style={{ fontSize: '12px', fontWeight: 600, color: 'var(--text-muted)' }}>STATUS:</label>
            <select
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value)}
              style={{
                backgroundColor: 'var(--bg-card)',
                color: 'var(--text-primary)',
                border: '1px solid var(--border)',
                borderRadius: '4px',
                padding: '6px 10px',
                fontSize: '13px'
              }}
            >
              <option value="">All Statuses</option>
              <option value="Delivered">Delivered</option>
              <option value="Failed">Failed</option>
              <option value="DeadLettered">Dead-Lettered</option>
              <option value="Pending">Pending</option>
              <option value="Processing">Processing</option>
              <option value="Skipped">Skipped</option>
            </select>
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: '8px', flex: 1, maxWidth: '320px' }}>
            <label style={{ fontSize: '12px', fontWeight: 600, color: 'var(--text-muted)' }}>EVENT TYPE:</label>
            <input
              type="text"
              placeholder="e.g. order.created"
              value={eventTypeFilter}
              onChange={(e) => setEventTypeFilter(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') fetchDeliveries();
              }}
              style={{
                flex: 1,
                backgroundColor: 'var(--bg-card)',
                color: 'var(--text-primary)',
                border: '1px solid var(--border)',
                borderRadius: '4px',
                padding: '6px 10px',
                fontSize: '13px'
              }}
            />
          </div>

          <button
            onClick={fetchDeliveries}
            style={{
              padding: '6px 12px',
              backgroundColor: 'var(--bg-card)',
              border: '1px solid var(--border)',
              color: 'var(--text-primary)',
              borderRadius: '4px',
              cursor: 'pointer',
              fontSize: '12px'
            }}
          >
            Apply Filter
          </button>
        </div>
      )}

      {/* Error alert */}
      {error && (
        <div style={{
          padding: '12px 16px',
          backgroundColor: 'rgba(239, 68, 68, 0.1)',
          border: '1px solid rgba(239, 68, 68, 0.25)',
          borderRadius: '8px',
          color: '#f87171',
          fontSize: '14px',
          display: 'flex',
          alignItems: 'center',
          gap: '10px'
        }}>
          <AlertTriangle size={18} />
          {error}
        </div>
      )}

      {/* Deliveries Table */}
      {apiKey.trim() && (
        <div style={{
          backgroundColor: 'var(--bg-secondary)',
          borderRadius: '10px',
          border: '1px solid var(--border)',
          overflow: 'hidden'
        }}>
          <table style={{ width: '100%', borderCollapse: 'collapse', textAlign: 'left', fontSize: '13px' }}>
            <thead>
              <tr style={{ borderBottom: '1px solid var(--border)', backgroundColor: 'var(--bg-card)', color: 'var(--text-muted)' }}>
                <th style={{ padding: '12px 16px', fontWeight: 600 }}>STATUS</th>
                <th style={{ padding: '12px 16px', fontWeight: 600 }}>EVENT TYPE</th>
                <th style={{ padding: '12px 16px', fontWeight: 600 }}>TARGET ENDPOINT</th>
                <th style={{ padding: '12px 16px', fontWeight: 600 }}>ATTEMPTS</th>
                <th style={{ padding: '12px 16px', fontWeight: 600 }}>DELIVERED AT</th>
                <th style={{ padding: '12px 16px', fontWeight: 600, textAlign: 'right' }}>ACTION</th>
              </tr>
            </thead>
            <tbody>
              {deliveries.length === 0 && !loading && (
                <tr>
                  <td colSpan={6} style={{ padding: '40px 16px', textAlign: 'center', color: 'var(--text-muted)' }}>
                    No webhook deliveries found matching the current filter.
                  </td>
                </tr>
              )}
              {deliveries.map((deliv) => (
                <tr
                  key={deliv.id}
                  style={{
                    borderBottom: '1px solid var(--border)',
                    transition: 'background-color 0.1s'
                  }}
                >
                  <td style={{ padding: '12px 16px' }}>
                    {renderStatusBadge(deliv.status, deliv.lastHttpStatusCode)}
                  </td>
                  <td style={{ padding: '12px 16px' }}>
                    <span style={{
                      padding: '2px 6px',
                      borderRadius: '4px',
                      fontSize: '11px',
                      backgroundColor: 'rgba(56, 189, 248, 0.1)',
                      color: 'var(--accent-cyan)',
                      fontFamily: 'monospace'
                    }}>
                      {deliv.eventType}
                    </span>
                  </td>
                  <td style={{ padding: '12px 16px' }}>
                    <div style={{ fontWeight: 600, color: 'var(--text-primary)', marginBottom: '2px' }}>
                      {deliv.endpointName}
                    </div>
                    <div style={{ fontFamily: 'monospace', fontSize: '11px', color: 'var(--text-muted)' }}>
                      {deliv.endpointUrl}
                    </div>
                  </td>
                  <td style={{ padding: '12px 16px', color: 'var(--text-secondary)' }}>
                    <div>{deliv.attemptCount} {deliv.attemptCount === 1 ? 'attempt' : 'attempts'}</div>
                    {deliv.replayCount > 0 && (
                      <div style={{ fontSize: '11px', color: 'var(--accent-cyan)' }}>
                        Replayed {deliv.replayCount}x
                      </div>
                    )}
                  </td>
                  <td style={{ padding: '12px 16px', color: 'var(--text-muted)', fontSize: '12px' }}>
                    {deliv.deliveredAtUtc
                      ? `${new Date(deliv.deliveredAtUtc).toLocaleDateString()} ${new Date(deliv.deliveredAtUtc).toLocaleTimeString()}`
                      : deliv.status === 'Pending' && deliv.nextAttemptAtUtc
                      ? `Retry: ${new Date(deliv.nextAttemptAtUtc).toLocaleTimeString()}`
                      : '—'}
                  </td>
                  <td style={{ padding: '12px 16px', textAlign: 'right' }}>
                    <div style={{ display: 'inline-flex', gap: '6px' }}>
                      {(deliv.status === 'DeadLettered' || deliv.status === 'Failed') && (
                        <button
                          onClick={() => openReplayModal(deliv)}
                          style={{
                            padding: '6px 10px',
                            backgroundColor: 'rgba(56, 189, 248, 0.1)',
                            border: '1px solid rgba(56, 189, 248, 0.3)',
                            color: 'var(--accent-cyan)',
                            borderRadius: '4px',
                            cursor: 'pointer',
                            display: 'inline-flex',
                            alignItems: 'center',
                            gap: '4px',
                            fontSize: '12px',
                            fontWeight: 600
                          }}
                        >
                          <RotateCcw size={12} />
                          Replay
                        </button>
                      )}
                      <button
                        onClick={() => setSelectedDelivery(deliv)}
                        style={{
                          padding: '6px 10px',
                          backgroundColor: 'var(--bg-card)',
                          border: '1px solid var(--border)',
                          color: 'var(--text-primary)',
                          borderRadius: '4px',
                          cursor: 'pointer',
                          display: 'inline-flex',
                          alignItems: 'center',
                          gap: '4px',
                          fontSize: '12px',
                          fontWeight: 500
                        }}
                      >
                        <Eye size={12} />
                        Inspect
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* INSPECT DELIVERY DRAWER / MODAL */}
      {selectedDelivery && (
        <div style={{
          position: 'fixed',
          top: 0,
          left: 0,
          right: 0,
          bottom: 0,
          backgroundColor: 'rgba(0, 0, 0, 0.75)',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          zIndex: 1000
        }}>
          <div style={{
            backgroundColor: 'var(--bg-secondary)',
            border: '1px solid var(--border)',
            borderRadius: '12px',
            width: '100%',
            maxWidth: '680px',
            maxHeight: '85vh',
            display: 'flex',
            flexDirection: 'column',
            boxShadow: '0 25px 30px -5px rgba(0, 0, 0, 0.6)'
          }}>
            {/* Modal Header */}
            <div style={{
              padding: '20px 24px',
              borderBottom: '1px solid var(--border)',
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'center'
            }}>
              <div>
                <h3 style={{ fontSize: '18px', fontWeight: 700, marginBottom: '4px' }}>
                  Delivery Details
                </h3>
                <div style={{ fontSize: '12px', color: 'var(--text-muted)', fontFamily: 'monospace' }}>
                  ID: {selectedDelivery.id}
                </div>
              </div>
              <div>
                {renderStatusBadge(selectedDelivery.status, selectedDelivery.lastHttpStatusCode)}
              </div>
            </div>

            {/* Modal Content */}
            <div style={{ padding: '24px', overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: '20px' }}>
              {/* Delivery Metadata Grid */}
              <div style={{
                display: 'grid',
                gridTemplateColumns: 'repeat(2, 1fr)',
                gap: '12px',
                backgroundColor: 'var(--bg-primary)',
                padding: '16px',
                borderRadius: '8px',
                border: '1px solid var(--border)',
                fontSize: '13px'
              }}>
                <div>
                  <span style={{ color: 'var(--text-muted)', fontSize: '11px', display: 'block' }}>EVENT TYPE</span>
                  <span style={{ fontFamily: 'monospace', color: 'var(--accent-cyan)', fontWeight: 600 }}>
                    {selectedDelivery.eventType}
                  </span>
                </div>
                <div>
                  <span style={{ color: 'var(--text-muted)', fontSize: '11px', display: 'block' }}>EVENT ID</span>
                  <span style={{ fontFamily: 'monospace', color: 'var(--text-secondary)' }}>
                    {selectedDelivery.eventId}
                  </span>
                </div>
                <div>
                  <span style={{ color: 'var(--text-muted)', fontSize: '11px', display: 'block' }}>TARGET ENDPOINT</span>
                  <span style={{ fontWeight: 600 }}>{selectedDelivery.endpointName}</span>
                </div>
                <div>
                  <span style={{ color: 'var(--text-muted)', fontSize: '11px', display: 'block' }}>ENDPOINT URL</span>
                  <span style={{ fontFamily: 'monospace', fontSize: '11px', color: 'var(--text-secondary)' }}>
                    {selectedDelivery.endpointUrl}
                  </span>
                </div>
                <div>
                  <span style={{ color: 'var(--text-muted)', fontSize: '11px', display: 'block' }}>ENQUEUE TIME</span>
                  <span style={{ color: 'var(--text-secondary)' }}>
                    {new Date(selectedDelivery.createdAtUtc).toLocaleString()}
                  </span>
                </div>
                <div>
                  <span style={{ color: 'var(--text-muted)', fontSize: '11px', display: 'block' }}>DELIVERY TIME</span>
                  <span style={{ color: 'var(--text-secondary)' }}>
                    {selectedDelivery.deliveredAtUtc ? new Date(selectedDelivery.deliveredAtUtc).toLocaleString() : 'In Progress'}
                  </span>
                </div>
                <div>
                  <span style={{ color: 'var(--text-muted)', fontSize: '11px', display: 'block' }}>ATTEMPTS & CLAIMS</span>
                  <span style={{ color: 'var(--text-secondary)' }}>
                    {selectedDelivery.attemptCount} attempts ({selectedDelivery.claimCount} claims)
                  </span>
                </div>
                <div>
                  <span style={{ color: 'var(--text-muted)', fontSize: '11px', display: 'block' }}>REPLAY COUNT</span>
                  <span style={{ color: selectedDelivery.replayCount > 0 ? 'var(--accent-cyan)' : 'var(--text-secondary)' }}>
                    {selectedDelivery.replayCount > 0 ? `${selectedDelivery.replayCount}x replayed` : '0'}
                  </span>
                </div>
                {selectedDelivery.lastErrorCategory && (
                  <div>
                    <span style={{ color: 'var(--text-muted)', fontSize: '11px', display: 'block' }}>FAILURE CATEGORY</span>
                    <span style={{ color: '#f87171', fontFamily: 'monospace' }}>
                      {selectedDelivery.lastErrorCategory}
                    </span>
                  </div>
                )}
                {selectedDelivery.status === 'Pending' && selectedDelivery.nextAttemptAtUtc && (
                  <div>
                    <span style={{ color: 'var(--text-muted)', fontSize: '11px', display: 'block' }}>NEXT RETRY SCHEDULED</span>
                    <span style={{ color: '#fbbf24', fontWeight: 600 }}>
                      {new Date(selectedDelivery.nextAttemptAtUtc).toLocaleString()}
                    </span>
                  </div>
                )}
              </div>

              {selectedDelivery.lastError && (
                <div style={{
                  padding: '12px 14px',
                  backgroundColor: 'rgba(239, 68, 68, 0.1)',
                  border: '1px solid rgba(239, 68, 68, 0.25)',
                  borderRadius: '6px',
                  color: '#f87171',
                  fontSize: '13px'
                }}>
                  <strong>Last Error:</strong> {selectedDelivery.lastError}
                </div>
              )}

              {/* Delivery Attempts Timeline */}
              <div>
                <h4 style={{ fontSize: '14px', fontWeight: 700, marginBottom: '12px', color: 'var(--text-primary)' }}>
                  Delivery Attempts ({selectedDelivery.attempts.length})
                </h4>

                {selectedDelivery.attempts.length === 0 ? (
                  <div style={{ color: 'var(--text-muted)', fontSize: '13px' }}>
                    No delivery attempts executed yet.
                  </div>
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '10px' }}>
                    {selectedDelivery.attempts.map((att) => (
                      <div
                        key={att.id}
                        style={{
                          backgroundColor: 'var(--bg-primary)',
                          border: '1px solid var(--border)',
                          borderRadius: '8px',
                          padding: '14px 16px'
                        }}
                      >
                        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '8px' }}>
                          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                            <span style={{
                              fontWeight: 700,
                              fontSize: '12px',
                              backgroundColor: 'var(--bg-card)',
                              padding: '2px 6px',
                              borderRadius: '4px'
                            }}>
                              #{att.attemptNumber}
                            </span>
                            <span style={{
                              fontSize: '12px',
                              fontWeight: 600,
                              color: att.outcome === 'Succeeded' ? '#4ade80' : '#f87171'
                            }}>
                              {att.outcome}
                            </span>
                          </div>

                          <div style={{ fontSize: '12px', color: 'var(--text-muted)', display: 'flex', gap: '12px' }}>
                            {att.httpStatusCode && (
                              <span>HTTP {att.httpStatusCode}</span>
                            )}
                            <span>{att.durationMs}ms</span>
                            <span>{new Date(att.completedAtUtc).toLocaleTimeString()}</span>
                          </div>
                        </div>

                        {att.responseExcerpt && (
                          <div style={{ marginTop: '8px' }}>
                            <span style={{ fontSize: '11px', color: 'var(--text-muted)', display: 'block', marginBottom: '4px' }}>
                              RESPONSE EXCERPT:
                            </span>
                            <pre style={{
                              backgroundColor: 'var(--bg-secondary)',
                              padding: '8px 10px',
                              borderRadius: '4px',
                              border: '1px solid var(--border)',
                              fontSize: '12px',
                              fontFamily: 'monospace',
                              color: 'var(--text-secondary)',
                              overflowX: 'auto',
                              margin: 0
                            }}>
                              {att.responseExcerpt}
                            </pre>
                          </div>
                        )}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            </div>

            {/* Modal Footer */}
            <div style={{
              padding: '16px 24px',
              borderTop: '1px solid var(--border)',
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'center'
            }}>
              <div>
                {(selectedDelivery.status === 'DeadLettered' || selectedDelivery.status === 'Failed') && (
                  <button
                    onClick={() => {
                      const target = selectedDelivery;
                      setSelectedDelivery(null);
                      openReplayModal(target);
                    }}
                    style={{
                      padding: '8px 16px',
                      backgroundColor: 'rgba(56, 189, 248, 0.15)',
                      border: '1px solid rgba(56, 189, 248, 0.3)',
                      color: 'var(--accent-cyan)',
                      borderRadius: '6px',
                      cursor: 'pointer',
                      fontSize: '13px',
                      fontWeight: 600,
                      display: 'inline-flex',
                      alignItems: 'center',
                      gap: '6px'
                    }}
                  >
                    <RotateCcw size={14} />
                    Replay Delivery
                  </button>
                )}
              </div>
              <button
                onClick={() => setSelectedDelivery(null)}
                style={{
                  padding: '8px 18px',
                  backgroundColor: 'var(--bg-card)',
                  border: '1px solid var(--border)',
                  color: 'var(--text-primary)',
                  borderRadius: '6px',
                  cursor: 'pointer',
                  fontSize: '13px',
                  fontWeight: 500
                }}
              >
                Close
              </button>
            </div>
          </div>
        </div>
      )}

      {/* REPLAY CONFIRMATION MODAL */}
      {replayTarget && (
        <div style={{
          position: 'fixed',
          top: 0,
          left: 0,
          right: 0,
          bottom: 0,
          backgroundColor: 'rgba(0, 0, 0, 0.75)',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          zIndex: 1100
        }}>
          <div style={{
            backgroundColor: 'var(--bg-secondary)',
            border: '1px solid var(--border)',
            borderRadius: '12px',
            width: '100%',
            maxWidth: '520px',
            boxShadow: '0 25px 30px -5px rgba(0, 0, 0, 0.6)',
            overflow: 'hidden'
          }}>
            <div style={{
              padding: '20px 24px',
              borderBottom: '1px solid var(--border)',
              display: 'flex',
              alignItems: 'center',
              gap: '10px'
            }}>
              <RotateCcw size={20} color="var(--accent-cyan)" />
              <h3 style={{ fontSize: '18px', fontWeight: 700, margin: 0 }}>
                Confirm Manual Replay
              </h3>
            </div>

            <form onSubmit={handleReplaySubmit} style={{ padding: '24px', display: 'flex', flexDirection: 'column', gap: '16px' }}>
              <p style={{ color: 'var(--text-secondary)', fontSize: '14px', margin: 0, lineHeight: 1.5 }}>
                You are about to replay delivery <code style={{ color: 'var(--accent-cyan)', fontFamily: 'monospace' }}>{replayTarget.id.substring(0, 8)}...</code>.
                This will reset its status to <strong>Pending</strong>, schedule it for immediate dispatch, and record an audit log. Existing attempt history will be preserved.
              </p>

              <div style={{
                backgroundColor: 'var(--bg-primary)',
                padding: '14px',
                borderRadius: '8px',
                border: '1px solid var(--border)',
                fontSize: '13px',
                display: 'flex',
                flexDirection: 'column',
                gap: '6px'
              }}>
                <div><strong>Event Type:</strong> <span style={{ fontFamily: 'monospace', color: 'var(--accent-cyan)' }}>{replayTarget.eventType}</span></div>
                <div><strong>Target Endpoint:</strong> {replayTarget.endpointName}</div>
                <div><strong>Current Status:</strong> {replayTarget.status}</div>
                <div><strong>Previous Attempts:</strong> {replayTarget.attemptCount} (Replayed {replayTarget.replayCount}x)</div>
                {replayTarget.lastError && (
                  <div style={{ color: '#f87171' }}><strong>Last Error:</strong> {replayTarget.lastError}</div>
                )}
              </div>

              <div>
                <label style={{ display: 'block', fontSize: '12px', fontWeight: 600, color: 'var(--text-muted)', marginBottom: '6px' }}>
                  IDEMPOTENCY KEY (OPTIONAL):
                </label>
                <input
                  type="text"
                  value={replayKey}
                  onChange={(e) => setReplayKey(e.target.value)}
                  placeholder="e.g. rep_manual_123"
                  style={{
                    width: '100%',
                    padding: '8px 12px',
                    borderRadius: '6px',
                    border: '1px solid var(--border)',
                    backgroundColor: 'var(--bg-primary)',
                    color: 'var(--text-primary)',
                    fontSize: '13px',
                    fontFamily: 'monospace'
                  }}
                />
                <span style={{ fontSize: '11px', color: 'var(--text-muted)', marginTop: '4px', display: 'block' }}>
                  Protects against accidental duplicate replays.
                </span>
              </div>

              {replayError && (
                <div style={{
                  padding: '10px 14px',
                  backgroundColor: 'rgba(239, 68, 68, 0.1)',
                  border: '1px solid rgba(239, 68, 68, 0.25)',
                  borderRadius: '6px',
                  color: '#f87171',
                  fontSize: '13px'
                }}>
                  {replayError}
                </div>
              )}

              {replaySuccess && (
                <div style={{
                  padding: '10px 14px',
                  backgroundColor: 'rgba(34, 197, 94, 0.1)',
                  border: '1px solid rgba(34, 197, 94, 0.25)',
                  borderRadius: '6px',
                  color: '#4ade80',
                  fontSize: '13px'
                }}>
                  {replaySuccess}
                </div>
              )}

              <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '10px', marginTop: '8px' }}>
                <button
                  type="button"
                  disabled={replayLoading}
                  onClick={() => setReplayTarget(null)}
                  style={{
                    padding: '8px 16px',
                    backgroundColor: 'var(--bg-card)',
                    border: '1px solid var(--border)',
                    color: 'var(--text-primary)',
                    borderRadius: '6px',
                    cursor: 'pointer',
                    fontSize: '13px'
                  }}
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={replayLoading}
                  style={{
                    padding: '8px 18px',
                    backgroundColor: 'var(--accent-cyan)',
                    color: '#090d16',
                    border: 'none',
                    borderRadius: '6px',
                    fontWeight: 600,
                    cursor: replayLoading ? 'not-allowed' : 'pointer',
                    fontSize: '13px',
                    display: 'inline-flex',
                    alignItems: 'center',
                    gap: '6px'
                  }}
                >
                  <RotateCcw size={14} />
                  {replayLoading ? 'Replaying...' : 'Confirm Replay'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
};
