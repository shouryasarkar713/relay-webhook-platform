import React, { useState } from 'react';
import { Sidebar, type NavTab } from './components/Sidebar';
import { Header } from './components/Header';
import { Overview } from './components/Overview';
import { Playground } from './components/Playground';
import { EventsView } from './components/EventsView';

export const App: React.FC = () => {
  const [currentTab, setCurrentTab] = useState<NavTab>('overview');
  // API key is strictly stored in React ephemeral memory
  const [apiKey, setApiKey] = useState<string>('');
  const [recentEventId, setRecentEventId] = useState<string | null>(null);

  const handleClearApiKey = () => {
    setApiKey('');
  };

  const handleEventCreated = (eventId: string) => {
    setRecentEventId(eventId);
  };

  return (
    <div style={{ display: 'flex', minHeight: '100vh', backgroundColor: 'var(--bg-primary)' }}>
      {/* Sidebar Navigation */}
      <Sidebar currentTab={currentTab} onTabChange={setCurrentTab} />

      {/* Main Content Area */}
      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 }}>
        <Header apiKey={apiKey} onClearApiKey={handleClearApiKey} />

        <main style={{ flex: 1, padding: '32px 40px', overflowY: 'auto' }}>
          {currentTab === 'overview' && <Overview onNavigate={setCurrentTab} />}
          {currentTab === 'playground' && (
            <Playground
              apiKey={apiKey}
              setApiKey={setApiKey}
              onEventCreated={handleEventCreated}
            />
          )}
          {currentTab === 'events' && (
            <EventsView
              apiKey={apiKey}
              setApiKey={setApiKey}
              selectedEventId={recentEventId}
            />
          )}
        </main>
      </div>
    </div>
  );
};
