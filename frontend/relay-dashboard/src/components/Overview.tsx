import React from 'react';
import { Database, ShieldCheck, Layers, ArrowRight, Zap } from 'lucide-react';
import type { NavTab } from './Sidebar';

interface OverviewProps {
  onNavigate: (tab: NavTab) => void;
}

export const Overview: React.FC<OverviewProps> = ({ onNavigate }) => {
  return (
    <div style={{ maxWidth: '1200px', margin: '0 auto', display: 'flex', flexDirection: 'column', gap: '32px' }}>
      {/* Hero */}
      <div style={{
        padding: '32px',
        borderRadius: '12px',
        backgroundColor: 'var(--bg-secondary)',
        border: '1px solid var(--border)',
        backgroundImage: 'radial-gradient(ellipse at top right, rgba(6, 182, 212, 0.12) 0%, transparent 60%)'
      }}>
        <div style={{ display: 'inline-flex', alignItems: 'center', gap: '6px', padding: '4px 10px', borderRadius: '20px', backgroundColor: 'rgba(56, 189, 248, 0.1)', color: 'var(--accent-cyan)', fontSize: '12px', fontWeight: 600, marginBottom: '16px' }}>
          <Zap size={14} />
          Phase 1 Active — First Working Vertical Slice
        </div>
        <h2 style={{ fontSize: '28px', fontWeight: 700, letterSpacing: '-0.03em', marginBottom: '12px' }}>
          Multi-Tenant Webhook Delivery & Event Platform
        </h2>
        <p style={{ color: 'var(--text-secondary)', fontSize: '15px', maxWidth: '780px', lineHeight: 1.6, marginBottom: '24px' }}>
          Relay durably accepts events through an authenticated, idempotent REST API, persists them in SQL Server
          alongside an atomic Transactional Outbox record, and guarantees reliable at-least-once downstream webhook delivery.
        </p>

        <div style={{ display: 'flex', gap: '12px' }}>
          <button
            onClick={() => onNavigate('playground')}
            style={{
              padding: '10px 18px',
              backgroundColor: 'var(--accent-cyan)',
              color: '#090d16',
              fontWeight: 600,
              fontSize: '14px',
              borderRadius: '6px',
              border: 'none',
              cursor: 'pointer',
              display: 'flex',
              alignItems: 'center',
              gap: '8px'
            }}
          >
            Open Event Playground
            <ArrowRight size={16} />
          </button>
          <button
            onClick={() => onNavigate('events')}
            style={{
              padding: '10px 18px',
              backgroundColor: 'var(--bg-card)',
              color: 'var(--text-primary)',
              fontWeight: 500,
              fontSize: '14px',
              borderRadius: '6px',
              border: '1px solid var(--border)',
              cursor: 'pointer'
            }}
          >
            View Ingested Events
          </button>
        </div>
      </div>

      {/* Core Architectural Pillars */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: '20px' }}>
        <div style={{
          backgroundColor: 'var(--bg-secondary)',
          border: '1px solid var(--border)',
          borderRadius: '10px',
          padding: '24px',
          display: 'flex',
          flexDirection: 'column',
          gap: '12px'
        }}>
          <div style={{ width: '40px', height: '40px', borderRadius: '8px', backgroundColor: 'rgba(6, 182, 212, 0.1)', display: 'flex', alignItems: 'center', justifyContent: 'center', color: 'var(--accent-cyan)' }}>
            <ShieldCheck size={22} />
          </div>
          <h3 style={{ fontSize: '16px', fontWeight: 600 }}>API-Key Authentication</h3>
          <p style={{ fontSize: '13px', color: 'var(--text-secondary)', lineHeight: 1.5 }}>
            Keys follow <code className="font-mono" style={{ color: 'var(--accent-cyan)' }}>rl_live_&lt;prefix&gt;_&lt;secret&gt;</code>.
            Only SHA-256 hashes are persisted; lookup is O(1) by prefix and verified using constant-time comparison.
          </p>
        </div>

        <div style={{
          backgroundColor: 'var(--bg-secondary)',
          border: '1px solid var(--border)',
          borderRadius: '10px',
          padding: '24px',
          display: 'flex',
          flexDirection: 'column',
          gap: '12px'
        }}>
          <div style={{ width: '40px', height: '40px', borderRadius: '8px', backgroundColor: 'rgba(59, 130, 246, 0.1)', display: 'flex', alignItems: 'center', justifyContent: 'center', color: 'var(--accent-blue)' }}>
            <Database size={22} />
          </div>
          <h3 style={{ fontSize: '16px', fontWeight: 600 }}>Database Idempotency</h3>
          <p style={{ fontSize: '13px', color: 'var(--text-secondary)', lineHeight: 1.5 }}>
            Guaranteed at the database level by a unique index on <code className="font-mono">(TenantId, IdempotencyKey)</code>.
            Prevents race conditions, safely returns existing events on match, or returns 409 on content conflict.
          </p>
        </div>

        <div style={{
          backgroundColor: 'var(--bg-secondary)',
          border: '1px solid var(--border)',
          borderRadius: '10px',
          padding: '24px',
          display: 'flex',
          flexDirection: 'column',
          gap: '12px'
        }}>
          <div style={{ width: '40px', height: '40px', borderRadius: '8px', backgroundColor: 'rgba(16, 185, 129, 0.1)', display: 'flex', alignItems: 'center', justifyContent: 'center', color: 'var(--accent-green)' }}>
            <Layers size={22} />
          </div>
          <h3 style={{ fontSize: '16px', fontWeight: 600 }}>Transactional Outbox</h3>
          <p style={{ fontSize: '13px', color: 'var(--text-secondary)', lineHeight: 1.5 }}>
            The business event and outbox record commit in the exact same SQL transaction.
            Eliminates the dual-write problem between database persistence and message broker dispatch.
          </p>
        </div>
      </div>

      {/* Vertical Slice Flow */}
      <div style={{
        backgroundColor: 'var(--bg-secondary)',
        border: '1px solid var(--border)',
        borderRadius: '10px',
        padding: '24px'
      }}>
        <h3 style={{ fontSize: '16px', fontWeight: 600, marginBottom: '16px' }}>
          Phase 1 Ingestion Pipeline Walkthrough
        </h3>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: '16px' }}>
          <div style={{ padding: '16px', backgroundColor: 'var(--bg-card)', borderRadius: '8px', border: '1px solid var(--border)' }}>
            <div style={{ fontSize: '12px', color: 'var(--accent-cyan)', fontWeight: 600, marginBottom: '6px' }}>STEP 1</div>
            <div style={{ fontWeight: 600, fontSize: '14px', marginBottom: '4px' }}>M2M Request</div>
            <div style={{ fontSize: '12px', color: 'var(--text-secondary)' }}>
              Producer sends POST /v1/events with X-Api-Key and Idempotency-Key headers.
            </div>
          </div>

          <div style={{ padding: '16px', backgroundColor: 'var(--bg-card)', borderRadius: '8px', border: '1px solid var(--border)' }}>
            <div style={{ fontSize: '12px', color: 'var(--accent-cyan)', fontWeight: 600, marginBottom: '6px' }}>STEP 2</div>
            <div style={{ fontWeight: 600, fontSize: '14px', marginBottom: '4px' }}>Auth & Scoping</div>
            <div style={{ fontSize: '12px', color: 'var(--text-secondary)' }}>
              Handler verifies key hash in constant time and binds validated TenantId to context.
            </div>
          </div>

          <div style={{ padding: '16px', backgroundColor: 'var(--bg-card)', borderRadius: '8px', border: '1px solid var(--border)' }}>
            <div style={{ fontSize: '12px', color: 'var(--accent-cyan)', fontWeight: 600, marginBottom: '6px' }}>STEP 3</div>
            <div style={{ fontWeight: 600, fontSize: '14px', marginBottom: '4px' }}>Atomic Outbox TX</div>
            <div style={{ fontSize: '12px', color: 'var(--text-secondary)' }}>
              Begins SQL transaction, inserts Event and OutboxMessage, enforces uniqueness constraint.
            </div>
          </div>

          <div style={{ padding: '16px', backgroundColor: 'var(--bg-card)', borderRadius: '8px', border: '1px solid var(--border)' }}>
            <div style={{ fontSize: '12px', color: 'var(--accent-cyan)', fontWeight: 600, marginBottom: '6px' }}>STEP 4</div>
            <div style={{ fontWeight: 600, fontSize: '14px', marginBottom: '4px' }}>202 Accepted</div>
            <div style={{ fontSize: '12px', color: 'var(--text-secondary)' }}>
              Returns HTTP 202 with Location header pointing to event resource. Ready for Phase 2 worker dispatch.
            </div>
          </div>
        </div>
      </div>
    </div>
  );
};
