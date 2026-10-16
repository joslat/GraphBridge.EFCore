import { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import type { SavedEvidence } from '../evidence';
import { App } from './views';
import './style.css';

declare function acquireVsCodeApi(): { postMessage(message: unknown): void };
const host = acquireVsCodeApi();
function Shell() {
  const [snapshot, setSnapshot] = useState<{ saved: SavedEvidence; revision: number }>();
  // The sole sender is the extension's validated projection, not file-controlled markup.
  useEffect(() => {
    const receive = (event: MessageEvent) => {
      if (event.data?.type === 'evidence') setSnapshot(previous => ({ saved: event.data.data as SavedEvidence, revision: (previous?.revision ?? 0) + 1 }));
    };
    window.addEventListener('message', receive);
    host.postMessage({ type: 'ready' });
    return () => window.removeEventListener('message', receive);
  }, []);
  return snapshot ? <App key={snapshot.revision} saved={snapshot.saved} /> : <main><h1>GraphBridge Query Lab</h1><p role="status">Loading saved evidence…</p></main>;
}
createRoot(document.getElementById('root')!).render(<Shell />);
