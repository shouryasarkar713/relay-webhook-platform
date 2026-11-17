import React, { useEffect, useState } from 'react';
import { apiClient, ApiError } from '../api/client';
import type { WebhookEndpointResponse, CreateWebhookEndpointRequest } from '../types/api';
import {
  Share2,
  Plus,
  RefreshCw,
  KeyRound,
  Copy,
  Check,
  AlertTriangle,
  CheckCircle2,
  XCircle,
  Edit2,
  Trash2,
  ShieldCheck,
  ExternalLink,
  Activity
} from 'lucide-react';

interface EndpointsViewProps {
  apiKey: string;
  setApiKey: (key: string) => void;
}

export const EndpointsView: React.FC<EndpointsViewProps> = ({ apiKey, setApiKey }) => {
  const [endpoints, setEndpoints] = useState<WebhookEndpointResponse[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Modal states
  const [isCreateModalOpen, setIsCreateModalOpen] = useState(false);
  const [createName, setCreateName] = useState('');
  const [createUrl, setCreateUrl] = useState('');
  const [createEventTypes, setCreateEventTypes] = useState('order.created, payment.received');
  const [createSubmitting, setCreateSubmitting] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);

  // One-time secret display modal
  const [createdSecret, setCreatedSecret] = useState<{ name: string; secret: string } | null>(null);
  const [secretCopied, setSecretCopied] = useState(false);

  // Edit modal
  const [editingEndpoint, setEditingEndpoint] = useState<WebhookEndpointResponse | null>(null);
  const [editName, setEditName] = useState('');
  const [editUrl, setEditUrl] = useState('');
  const [editEventTypes, setEditEventTypes] = useState('');
  const [editActive, setEditActive] = useState(true);
  const [editSubmitting, setEditSubmitting] = useState(false);
  const [editError, setEditError] = useState<string | null>(null);

  const fetchEndpoints = async () => {
    if (!apiKey.trim()) return;

    setLoading(true);
    setError(null);
    try {
      const data = await apiClient.listEndpoints(apiKey.trim());
      setEndpoints(data);
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
      fetchEndpoints();
    }
  }, [apiKey]);

  const handleCreateSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!apiKey.trim()) return;

    setCreateSubmitting(true);
    setCreateError(null);

    const eventTypes = createEventTypes
      .split(',')
      .map(s => s.trim())
      .filter(s => s.length > 0);

    const request: CreateWebhookEndpointRequest = {
      name: createName.trim(),
      url: createUrl.trim(),
      eventTypes
    };

    try {
      const created = await apiClient.createEndpoint(apiKey.trim(), request);
      setIsCreateModalOpen(false);
      setCreateName('');
      setCreateUrl('');
      setCreateEventTypes('order.created, payment.received');

      if (created.signingSecret) {
        setCreatedSecret({ name: created.name, secret: created.signingSecret });
        setSecretCopied(false);
      }

      await fetchEndpoints();
    } catch (err) {
      if (err instanceof ApiError) {
        setCreateError(err.problem.detail || err.problem.title);
      } else {
        setCreateError((err as Error).message);
      }
    } finally {
      setCreateSubmitting(false);
    }
  };

  const handleCopySecret = async () => {
    if (!createdSecret) return;
    await navigator.clipboard.writeText(createdSecret.secret);
    setSecretCopied(true);
    setTimeout(() => setSecretCopied(false), 2000);
  };

  const openEditModal = (ep: WebhookEndpointResponse) => {
    setEditingEndpoint(ep);
    setEditName(ep.name);
    setEditUrl(ep.url);
    setEditEventTypes(ep.eventTypes.join(', '));
    setEditActive(ep.isActive);
    setEditError(null);
  };

  const handleEditSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!editingEndpoint || !apiKey.trim()) return;

    setEditSubmitting(true);
    setEditError(null);

    const eventTypes = editEventTypes
      .split(',')
      .map(s => s.trim())
      .filter(s => s.length > 0);

    try {
      await apiClient.updateEndpoint(apiKey.trim(), editingEndpoint.id, {
        name: editName.trim(),
        url: editUrl.trim(),
        eventTypes,
        isActive: editActive
      });
      setEditingEndpoint(null);
      await fetchEndpoints();
    } catch (err) {
      if (err instanceof ApiError) {
        setEditError(err.problem.detail || err.problem.title);
      } else {
        setEditError((err as Error).message);
      }
    } finally {
      setEditSubmitting(false);
    }
  };

  const handleDeleteEndpoint = async (ep: WebhookEndpointResponse) => {
    if (!apiKey.trim()) return;
    const confirm = window.confirm(`Are you sure you want to deactivate endpoint "${ep.name}"?`);
    if (!confirm) return;

    try {
      await apiClient.deleteEndpoint(apiKey.trim(), ep.id);
      await fetchEndpoints();
    } catch (err) {
      alert(`Failed to deactivate endpoint: ${(err as Error).message}`);
    }
  };

  return (
    <div style={{ maxWidth: '1200px', margin: '0 auto', display: 'flex', flexDirection: 'column', gap: '20px' }}>
      {/* Top Banner / Controls */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div>
          <h2 style={{ fontSize: '24px', fontWeight: 700, letterSpacing: '-0.02em', marginBottom: '4px' }}>
            Webhook Endpoints
          </h2>
          <p style={{ color: 'var(--text-secondary)', fontSize: '14px' }}>
            Register customer endpoints to receive matching events with cryptographic HMAC-SHA256 signatures.
          </p>
        </div>

        <div style={{ display: 'flex', gap: '10px' }}>
          <button
            onClick={fetchEndpoints}
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

          <button
            onClick={() => setIsCreateModalOpen(true)}
            disabled={!apiKey.trim()}
            style={{
              padding: '8px 16px',
              backgroundColor: 'var(--accent-cyan)',
              color: '#090d16',
              border: 'none',
              borderRadius: '6px',
              cursor: !apiKey.trim() ? 'not-allowed' : 'pointer',
              display: 'flex',
              alignItems: 'center',
              gap: '6px',
              fontSize: '13px',
              fontWeight: 600
            }}
          >
            <Plus size={16} />
            Create Endpoint
          </button>
        </div>
      </div>

      {/* No API Key Prompt */}
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
            Enter your tenant API key to manage your webhook endpoints.
          </p>
          <div style={{ display: 'flex', gap: '8px', maxWidth: '420px', margin: '0 auto' }}>
            <input
              type="password"
              placeholder="rl_live_..."
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  setApiKey((e.target as HTMLInputElement).value);
                }
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

      {/* Endpoints Table */}
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
                <th style={{ padding: '12px 16px', fontWeight: 600 }}>NAME</th>
                <th style={{ padding: '12px 16px', fontWeight: 600 }}>STATUS</th>
                <th style={{ padding: '12px 16px', fontWeight: 600 }}>CIRCUIT</th>
                <th style={{ padding: '12px 16px', fontWeight: 600 }}>TARGET URL</th>
                <th style={{ padding: '12px 16px', fontWeight: 600 }}>SUBSCRIPTIONS</th>
                <th style={{ padding: '12px 16px', fontWeight: 600 }}>CREATED</th>
                <th style={{ padding: '12px 16px', fontWeight: 600, textAlign: 'right' }}>ACTIONS</th>
              </tr>
            </thead>
            <tbody>
              {endpoints.length === 0 && !loading && (
                <tr>
                  <td colSpan={7} style={{ padding: '40px 16px', textAlign: 'center', color: 'var(--text-muted)' }}>
                    No webhook endpoints registered yet. Click &ldquo;Create Endpoint&rdquo; to add one.
                  </td>
                </tr>
              )}
              {endpoints.map((ep) => (
                <tr
                  key={ep.id}
                  style={{
                    borderBottom: '1px solid var(--border)',
                    opacity: ep.isActive ? 1 : 0.65,
                    transition: 'background-color 0.1s'
                  }}
                >
                  <td style={{ padding: '12px 16px', fontWeight: 600, color: 'var(--text-primary)' }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                      <Share2 size={16} color="var(--accent-cyan)" />
                      {ep.name}
                    </div>
                  </td>
                  <td style={{ padding: '12px 16px' }}>
                    {ep.isActive ? (
                      <span style={{
                        display: 'inline-flex',
                        alignItems: 'center',
                        gap: '4px',
                        padding: '2px 8px',
                        borderRadius: '12px',
                        fontSize: '11px',
                        fontWeight: 600,
                        backgroundColor: 'rgba(34, 197, 94, 0.1)',
                        color: '#4ade80'
                      }}>
                        <CheckCircle2 size={12} />
                        Active
                      </span>
                    ) : (
                      <span style={{
                        display: 'inline-flex',
                        alignItems: 'center',
                        gap: '4px',
                        padding: '2px 8px',
                        borderRadius: '12px',
                        fontSize: '11px',
                        fontWeight: 600,
                        backgroundColor: 'rgba(239, 68, 68, 0.1)',
                        color: '#f87171'
                      }}>
                        <XCircle size={12} />
                        Deactivated
                      </span>
                    )}
                  </td>
                  <td style={{ padding: '12px 16px' }}>
                    {ep.circuitState === 'Closed' && (
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
                        Closed
                      </span>
                    )}
                    {ep.circuitState === 'Open' && (
                      <span style={{
                        display: 'inline-flex',
                        alignItems: 'center',
                        gap: '4px',
                        padding: '2px 8px',
                        borderRadius: '12px',
                        fontSize: '11px',
                        fontWeight: 600,
                        backgroundColor: 'rgba(239, 68, 68, 0.15)',
                        color: '#f87171',
                        border: '1px solid rgba(239, 68, 68, 0.3)'
                      }} title={`Failing endpoint: ${ep.consecutiveFailureCount} consecutive failures`}>
                        <AlertTriangle size={12} />
                        Open ({ep.consecutiveFailureCount} fail{ep.consecutiveFailureCount === 1 ? '' : 's'})
                      </span>
                    )}
                    {ep.circuitState === 'HalfOpen' && (
                      <span style={{
                        display: 'inline-flex',
                        alignItems: 'center',
                        gap: '4px',
                        padding: '2px 8px',
                        borderRadius: '12px',
                        fontSize: '11px',
                        fontWeight: 600,
                        backgroundColor: 'rgba(234, 179, 8, 0.15)',
                        color: '#fbbf24',
                        border: '1px solid rgba(234, 179, 8, 0.3)'
                      }}>
                        <Activity size={12} />
                        Half-Open
                      </span>
                    )}
                  </td>
                  <td style={{ padding: '12px 16px', fontFamily: 'monospace', color: 'var(--text-secondary)' }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
                      <span>{ep.url}</span>
                      <a href={ep.url} target="_blank" rel="noreferrer" style={{ color: 'var(--text-muted)' }}>
                        <ExternalLink size={12} />
                      </a>
                    </div>
                  </td>
                  <td style={{ padding: '12px 16px' }}>
                    <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px' }}>
                      {ep.eventTypes.map(t => (
                        <span key={t} style={{
                          padding: '2px 6px',
                          borderRadius: '4px',
                          fontSize: '11px',
                          backgroundColor: 'rgba(56, 189, 248, 0.1)',
                          color: 'var(--accent-cyan)',
                          fontFamily: 'monospace'
                        }}>
                          {t}
                        </span>
                      ))}
                    </div>
                  </td>
                  <td style={{ padding: '12px 16px', color: 'var(--text-muted)', fontSize: '12px' }}>
                    {new Date(ep.createdAtUtc).toLocaleDateString()} {new Date(ep.createdAtUtc).toLocaleTimeString()}
                  </td>
                  <td style={{ padding: '12px 16px', textAlign: 'right' }}>
                    <div style={{ display: 'inline-flex', gap: '8px' }}>
                      <button
                        onClick={() => openEditModal(ep)}
                        title="Edit Endpoint"
                        style={{
                          padding: '6px 8px',
                          backgroundColor: 'var(--bg-card)',
                          border: '1px solid var(--border)',
                          borderRadius: '4px',
                          color: 'var(--text-primary)',
                          cursor: 'pointer'
                        }}
                      >
                        <Edit2 size={13} />
                      </button>
                      {ep.isActive && (
                        <button
                          onClick={() => handleDeleteEndpoint(ep)}
                          title="Deactivate Endpoint"
                          style={{
                            padding: '6px 8px',
                            backgroundColor: 'rgba(239, 68, 68, 0.1)',
                            border: '1px solid rgba(239, 68, 68, 0.25)',
                            borderRadius: '4px',
                            color: '#f87171',
                            cursor: 'pointer'
                          }}
                        >
                          <Trash2 size={13} />
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* CREATE ENDPOINT MODAL */}
      {isCreateModalOpen && (
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
            maxWidth: '520px',
            padding: '24px',
            boxShadow: '0 20px 25px -5px rgba(0, 0, 0, 0.5)'
          }}>
            <h3 style={{ fontSize: '18px', fontWeight: 700, marginBottom: '6px' }}>Register Webhook Endpoint</h3>
            <p style={{ color: 'var(--text-secondary)', fontSize: '13px', marginBottom: '20px' }}>
              Relay will deliver matching events to this URL with HMAC-SHA256 signatures.
            </p>

            {createError && (
              <div style={{
                padding: '10px 14px',
                backgroundColor: 'rgba(239, 68, 68, 0.1)',
                border: '1px solid rgba(239, 68, 68, 0.3)',
                borderRadius: '6px',
                color: '#f87171',
                fontSize: '13px',
                marginBottom: '16px'
              }}>
                {createError}
              </div>
            )}

            <form onSubmit={handleCreateSubmit} style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
              <div>
                <label style={{ display: 'block', fontSize: '12px', fontWeight: 600, color: 'var(--text-muted)', marginBottom: '6px' }}>
                  ENDPOINT NAME
                </label>
                <input
                  type="text"
                  required
                  placeholder="e.g. Production Billing Webhook"
                  value={createName}
                  onChange={(e) => setCreateName(e.target.value)}
                  style={{
                    width: '100%',
                    padding: '8px 12px',
                    borderRadius: '6px',
                    border: '1px solid var(--border)',
                    backgroundColor: 'var(--bg-primary)',
                    color: 'var(--text-primary)',
                    fontSize: '13px'
                  }}
                />
              </div>

              <div>
                <label style={{ display: 'block', fontSize: '12px', fontWeight: 600, color: 'var(--text-muted)', marginBottom: '6px' }}>
                  WEBHOOK URL (HTTPS Required in Production)
                </label>
                <input
                  type="url"
                  required
                  placeholder="https://api.yourdomain.com/webhooks"
                  value={createUrl}
                  onChange={(e) => setCreateUrl(e.target.value)}
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
                <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginTop: '4px' }}>
                  Private/internal IP ranges, loopback addresses, and cloud metadata endpoints are strictly blocked by SSRF defense.
                </div>
              </div>

              <div>
                <label style={{ display: 'block', fontSize: '12px', fontWeight: 600, color: 'var(--text-muted)', marginBottom: '6px' }}>
                  SUBSCRIBED EVENT TYPES (Comma Separated)
                </label>
                <input
                  type="text"
                  required
                  placeholder="order.created, payment.received"
                  value={createEventTypes}
                  onChange={(e) => setCreateEventTypes(e.target.value)}
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
              </div>

              <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '10px', marginTop: '12px' }}>
                <button
                  type="button"
                  onClick={() => setIsCreateModalOpen(false)}
                  style={{
                    padding: '8px 14px',
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
                  disabled={createSubmitting}
                  style={{
                    padding: '8px 18px',
                    backgroundColor: 'var(--accent-cyan)',
                    color: '#090d16',
                    border: 'none',
                    borderRadius: '6px',
                    cursor: createSubmitting ? 'not-allowed' : 'pointer',
                    fontWeight: 600,
                    fontSize: '13px'
                  }}
                >
                  {createSubmitting ? 'Creating...' : 'Create Endpoint'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* ONE-TIME SIGNING SECRET DISPLAY DIALOG */}
      {createdSecret && (
        <div style={{
          position: 'fixed',
          top: 0,
          left: 0,
          right: 0,
          bottom: 0,
          backgroundColor: 'rgba(0, 0, 0, 0.85)',
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
            maxWidth: '560px',
            padding: '28px',
            boxShadow: '0 25px 30px -5px rgba(0, 0, 0, 0.6)'
          }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: '10px', color: '#fbbf24', marginBottom: '14px' }}>
              <ShieldCheck size={24} />
              <h3 style={{ fontSize: '18px', fontWeight: 700, margin: 0, color: 'var(--text-primary)' }}>
                Endpoint Created &mdash; Save Signing Secret
              </h3>
            </div>

            <div style={{
              padding: '12px 16px',
              backgroundColor: 'rgba(251, 191, 36, 0.1)',
              border: '1px solid rgba(251, 191, 36, 0.3)',
              borderRadius: '8px',
              color: '#fbbf24',
              fontSize: '13px',
              lineHeight: 1.5,
              marginBottom: '18px'
            }}>
              <strong>Important Security Notice:</strong> This signing secret is shown <em>only once</em> and cannot be retrieved later. Relay stores it encrypted with AES-256 via ASP.NET Core Data Protection and never returns it on subsequent queries.
            </div>

            <div style={{ marginBottom: '20px' }}>
              <label style={{ display: 'block', fontSize: '11px', fontWeight: 600, color: 'var(--text-muted)', marginBottom: '6px' }}>
                SIGNING SECRET ({createdSecret.name})
              </label>
              <div style={{
                display: 'flex',
                alignItems: 'center',
                backgroundColor: 'var(--bg-primary)',
                border: '1px solid var(--border)',
                borderRadius: '6px',
                padding: '10px 14px',
                gap: '10px'
              }}>
                <code style={{
                  flex: 1,
                  fontFamily: 'monospace',
                  color: 'var(--accent-cyan)',
                  fontSize: '14px',
                  wordBreak: 'break-all'
                }}>
                  {createdSecret.secret}
                </code>
                <button
                  onClick={handleCopySecret}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    gap: '6px',
                    padding: '6px 12px',
                    backgroundColor: secretCopied ? 'rgba(34, 197, 94, 0.2)' : 'var(--bg-card)',
                    color: secretCopied ? '#4ade80' : 'var(--text-primary)',
                    border: '1px solid var(--border)',
                    borderRadius: '4px',
                    cursor: 'pointer',
                    fontSize: '12px',
                    fontWeight: 500
                  }}
                >
                  {secretCopied ? <Check size={14} /> : <Copy size={14} />}
                  {secretCopied ? 'Copied!' : 'Copy'}
                </button>
              </div>
            </div>

            <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
              <button
                onClick={() => setCreatedSecret(null)}
                style={{
                  padding: '9px 20px',
                  backgroundColor: 'var(--accent-cyan)',
                  color: '#090d16',
                  border: 'none',
                  borderRadius: '6px',
                  fontWeight: 600,
                  fontSize: '13px',
                  cursor: 'pointer'
                }}
              >
                I have stored this secret safely
              </button>
            </div>
          </div>
        </div>
      )}

      {/* EDIT MODAL */}
      {editingEndpoint && (
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
            maxWidth: '500px',
            padding: '24px'
          }}>
            <h3 style={{ fontSize: '18px', fontWeight: 700, marginBottom: '16px' }}>Edit Endpoint</h3>

            {editError && (
              <div style={{
                padding: '10px 14px',
                backgroundColor: 'rgba(239, 68, 68, 0.1)',
                border: '1px solid rgba(239, 68, 68, 0.3)',
                borderRadius: '6px',
                color: '#f87171',
                fontSize: '13px',
                marginBottom: '16px'
              }}>
                {editError}
              </div>
            )}

            <form onSubmit={handleEditSubmit} style={{ display: 'flex', flexDirection: 'column', gap: '14px' }}>
              <div>
                <label style={{ display: 'block', fontSize: '12px', fontWeight: 600, color: 'var(--text-muted)', marginBottom: '4px' }}>
                  NAME
                </label>
                <input
                  type="text"
                  required
                  value={editName}
                  onChange={(e) => setEditName(e.target.value)}
                  style={{
                    width: '100%',
                    padding: '8px 12px',
                    borderRadius: '6px',
                    border: '1px solid var(--border)',
                    backgroundColor: 'var(--bg-primary)',
                    color: 'var(--text-primary)',
                    fontSize: '13px'
                  }}
                />
              </div>

              <div>
                <label style={{ display: 'block', fontSize: '12px', fontWeight: 600, color: 'var(--text-muted)', marginBottom: '4px' }}>
                  URL
                </label>
                <input
                  type="url"
                  required
                  value={editUrl}
                  onChange={(e) => setEditUrl(e.target.value)}
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
              </div>

              <div>
                <label style={{ display: 'block', fontSize: '12px', fontWeight: 600, color: 'var(--text-muted)', marginBottom: '4px' }}>
                  EVENT SUBSCRIPTIONS
                </label>
                <input
                  type="text"
                  required
                  value={editEventTypes}
                  onChange={(e) => setEditEventTypes(e.target.value)}
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
              </div>

              <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginTop: '4px' }}>
                <input
                  type="checkbox"
                  id="activeCheckbox"
                  checked={editActive}
                  onChange={(e) => setEditActive(e.target.checked)}
                />
                <label htmlFor="activeCheckbox" style={{ fontSize: '13px', color: 'var(--text-primary)', cursor: 'pointer' }}>
                  Active (Receiving events)
                </label>
              </div>

              <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '10px', marginTop: '10px' }}>
                <button
                  type="button"
                  onClick={() => setEditingEndpoint(null)}
                  style={{
                    padding: '8px 14px',
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
                  disabled={editSubmitting}
                  style={{
                    padding: '8px 16px',
                    backgroundColor: 'var(--accent-cyan)',
                    color: '#090d16',
                    border: 'none',
                    borderRadius: '6px',
                    fontWeight: 600,
                    fontSize: '13px',
                    cursor: editSubmitting ? 'not-allowed' : 'pointer'
                  }}
                >
                  {editSubmitting ? 'Saving...' : 'Save Changes'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
};
