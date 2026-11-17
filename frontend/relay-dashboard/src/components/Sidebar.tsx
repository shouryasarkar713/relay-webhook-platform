import React from 'react';
import { LayoutDashboard, Radio, Play, Share2, Send, Settings, ShieldCheck } from 'lucide-react';

export type NavTab = 'overview' | 'events' | 'playground' | 'endpoints' | 'deliveries';

interface SidebarProps {
  currentTab: NavTab;
  onTabChange: (tab: NavTab) => void;
}

export const Sidebar: React.FC<SidebarProps> = ({ currentTab, onTabChange }) => {
  return (
    <aside style={{
      width: '260px',
      backgroundColor: 'var(--bg-secondary)',
      borderRight: '1px solid var(--border)',
      display: 'flex',
      flexDirection: 'column',
      minHeight: '100vh',
      flexShrink: 0
    }}>
      {/* Brand Header */}
      <div style={{
        padding: '24px 20px',
        borderBottom: '1px solid var(--border)',
        display: 'flex',
        alignItems: 'center',
        gap: '12px'
      }}>
        <div style={{
          width: '36px',
          height: '36px',
          borderRadius: '8px',
          background: 'linear-gradient(135deg, #06b6d4 0%, #3b82f6 100%)',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          color: '#ffffff',
          fontWeight: 'bold'
        }}>
          <Radio size={20} />
        </div>
        <div>
          <div style={{ fontWeight: 700, fontSize: '18px', letterSpacing: '-0.02em' }}>RELAY</div>
          <div style={{ fontSize: '11px', color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
            Event Platform
          </div>
        </div>
      </div>

      {/* Navigation Links */}
      <nav style={{ padding: '16px 12px', display: 'flex', flexDirection: 'column', gap: '4px', flex: 1 }}>
        <button
          onClick={() => onTabChange('overview')}
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '12px',
            padding: '10px 14px',
            borderRadius: '6px',
            border: 'none',
            cursor: 'pointer',
            textAlign: 'left',
            fontSize: '14px',
            fontWeight: 500,
            color: currentTab === 'overview' ? '#ffffff' : 'var(--text-secondary)',
            backgroundColor: currentTab === 'overview' ? 'var(--bg-card)' : 'transparent',
            transition: 'all 0.15s ease'
          }}
        >
          <LayoutDashboard size={18} color={currentTab === 'overview' ? 'var(--accent-cyan)' : 'currentColor'} />
          Overview
        </button>

        <button
          onClick={() => onTabChange('events')}
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '12px',
            padding: '10px 14px',
            borderRadius: '6px',
            border: 'none',
            cursor: 'pointer',
            textAlign: 'left',
            fontSize: '14px',
            fontWeight: 500,
            color: currentTab === 'events' ? '#ffffff' : 'var(--text-secondary)',
            backgroundColor: currentTab === 'events' ? 'var(--bg-card)' : 'transparent',
            transition: 'all 0.15s ease'
          }}
        >
          <Radio size={18} color={currentTab === 'events' ? 'var(--accent-cyan)' : 'currentColor'} />
          Events
        </button>

        <button
          onClick={() => onTabChange('playground')}
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '12px',
            padding: '10px 14px',
            borderRadius: '6px',
            border: 'none',
            cursor: 'pointer',
            textAlign: 'left',
            fontSize: '14px',
            fontWeight: 500,
            color: currentTab === 'playground' ? '#ffffff' : 'var(--text-secondary)',
            backgroundColor: currentTab === 'playground' ? 'var(--bg-card)' : 'transparent',
            transition: 'all 0.15s ease'
          }}
        >
          <Play size={18} color={currentTab === 'playground' ? 'var(--accent-cyan)' : 'currentColor'} />
          Event Playground
        </button>

        <button
          onClick={() => onTabChange('endpoints')}
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '12px',
            padding: '10px 14px',
            borderRadius: '6px',
            border: 'none',
            cursor: 'pointer',
            textAlign: 'left',
            fontSize: '14px',
            fontWeight: 500,
            color: currentTab === 'endpoints' ? '#ffffff' : 'var(--text-secondary)',
            backgroundColor: currentTab === 'endpoints' ? 'var(--bg-card)' : 'transparent',
            transition: 'all 0.15s ease'
          }}
        >
          <Share2 size={18} color={currentTab === 'endpoints' ? 'var(--accent-cyan)' : 'currentColor'} />
          Endpoints
        </button>

        <button
          onClick={() => onTabChange('deliveries')}
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '12px',
            padding: '10px 14px',
            borderRadius: '6px',
            border: 'none',
            cursor: 'pointer',
            textAlign: 'left',
            fontSize: '14px',
            fontWeight: 500,
            color: currentTab === 'deliveries' ? '#ffffff' : 'var(--text-secondary)',
            backgroundColor: currentTab === 'deliveries' ? 'var(--bg-card)' : 'transparent',
            transition: 'all 0.15s ease'
          }}
        >
          <Send size={18} color={currentTab === 'deliveries' ? 'var(--accent-cyan)' : 'currentColor'} />
          Deliveries
        </button>

        <div style={{ height: '1px', backgroundColor: 'var(--border)', margin: '12px 6px' }} />

        {/* Coming Soon items */}
        <div style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          padding: '10px 14px',
          color: 'var(--text-muted)',
          fontSize: '14px',
          cursor: 'not-allowed'
        }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
            <Settings size={18} />
            Settings
          </div>
          <span style={{
            fontSize: '10px',
            textTransform: 'uppercase',
            padding: '2px 6px',
            backgroundColor: 'rgba(100, 116, 139, 0.2)',
            borderRadius: '4px',
            fontWeight: 600
          }}>Soon</span>
        </div>
      </nav>

      {/* Security badge footer */}
      <div style={{
        padding: '16px',
        margin: '12px',
        borderRadius: '8px',
        backgroundColor: 'rgba(6, 182, 212, 0.05)',
        border: '1px solid rgba(6, 182, 212, 0.15)',
        fontSize: '12px',
        color: 'var(--text-secondary)'
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '6px', color: 'var(--accent-cyan)', fontWeight: 600, marginBottom: '4px' }}>
          <ShieldCheck size={14} />
          In-Memory Keys
        </div>
        Keys stay in browser memory and are never persisted to web storage.
      </div>
    </aside>
  );
};
