import React, { useEffect, useState } from 'react';
import { apiClient } from '../api/client';
import type { HealthStatus } from '../types/api';
import { KeyRound, CheckCircle2, AlertCircle, RefreshCw } from 'lucide-react';

interface HeaderProps {
  apiKey: string;
  onClearApiKey: () => void;
}

export const Header: React.FC<HeaderProps> = ({ apiKey, onClearApiKey }) => {
  const [health, setHealth] = useState<HealthStatus | null>(null);
  const [checking, setChecking] = useState(false);

  const checkHealth = async () => {
    setChecking(true);
    try {
      const res = await apiClient.checkReady();
      setHealth(res);
    } catch {
      setHealth({
        status: 'Unreachable',
        database: 'Offline',
        timestampUtc: new Date().toISOString()
      });
    } finally {
      setChecking(false);
    }
  };

  useEffect(() => {
    checkHealth();
    const interval = setInterval(checkHealth, 30000);
    return () => clearInterval(interval);
  }, []);

  const isHealthy = health?.status === 'Ready';

  return (
    <header style={{
      height: '64px',
      borderBottom: '1px solid var(--border)',
      backgroundColor: 'var(--bg-secondary)',
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between',
      padding: '0 24px',
      flexShrink: 0
    }}>
      {/* Title / status */}
      <div style={{ display: 'flex', alignItems: 'center', gap: '16px' }}>
        <h1 style={{ fontSize: '16px', fontWeight: 600 }}>Developer Console</h1>
        <div style={{
          display: 'flex',
          alignItems: 'center',
          gap: '6px',
          padding: '4px 10px',
          borderRadius: '20px',
          backgroundColor: isHealthy ? 'rgba(16, 185, 129, 0.1)' : 'rgba(239, 68, 68, 0.1)',
          border: `1px solid ${isHealthy ? 'rgba(16, 185, 129, 0.2)' : 'rgba(239, 68, 68, 0.2)'}`,
          fontSize: '12px',
          color: isHealthy ? 'var(--accent-green)' : 'var(--accent-red)'
        }}>
          {isHealthy ? <CheckCircle2 size={13} /> : <AlertCircle size={13} />}
          <span>{isHealthy ? 'API & SQL Server Connected' : 'API Connecting...'}</span>
          <button
            onClick={checkHealth}
            title="Refresh status"
            style={{
              background: 'none',
              border: 'none',
              color: 'inherit',
              cursor: 'pointer',
              display: 'flex',
              padding: 0
            }}
          >
            <RefreshCw size={11} className={checking ? 'animate-spin' : ''} />
          </button>
        </div>
      </div>

      {/* API Key state */}
      <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
        {apiKey ? (
          <div style={{
            display: 'flex',
            alignItems: 'center',
            gap: '8px',
            backgroundColor: 'var(--bg-card)',
            padding: '6px 12px',
            borderRadius: '6px',
            border: '1px solid var(--border)',
            fontSize: '13px'
          }}>
            <KeyRound size={14} color="var(--accent-cyan)" />
            <span style={{ color: 'var(--text-secondary)' }}>Key:</span>
            <span className="font-mono" style={{ color: 'var(--text-primary)' }}>
              {apiKey.slice(0, 15)}...
            </span>
            <button
              onClick={onClearApiKey}
              style={{
                background: 'none',
                border: 'none',
                color: 'var(--text-muted)',
                cursor: 'pointer',
                fontSize: '12px',
                marginLeft: '6px',
                textDecoration: 'underline'
              }}
            >
              Clear
            </button>
          </div>
        ) : (
          <div style={{
            fontSize: '13px',
            color: 'var(--accent-amber)',
            backgroundColor: 'rgba(245, 158, 11, 0.1)',
            padding: '6px 12px',
            borderRadius: '6px',
            border: '1px solid rgba(245, 158, 11, 0.2)'
          }}>
            No API Key active in session
          </div>
        )}
      </div>
    </header>
  );
};
