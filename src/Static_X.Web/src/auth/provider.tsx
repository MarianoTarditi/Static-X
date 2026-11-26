import { createContext, useContext, useEffect, useSyncExternalStore, type ReactNode } from 'react';
import { QueryClientProvider } from '@tanstack/react-query';
import type { Session } from './session';

const SessionContext = createContext<Session | null>(null);

export function SessionProvider({ session, children }: { session: Session; children: ReactNode }) {
  useEffect(() => { session.start(); }, [session]);
  return <SessionContext.Provider value={session}><QueryClientProvider client={session.queryClient}>{children}</QueryClientProvider></SessionContext.Provider>;
}

export function useSession() {
  const session = useContext(SessionContext);
  if (!session) throw new Error('SessionProvider is required');
  const state = useSyncExternalStore(session.subscribe, session.getSnapshot);
  return { session, state };
}
