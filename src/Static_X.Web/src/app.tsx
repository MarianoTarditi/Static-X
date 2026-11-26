import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import { SessionProvider, useSession } from '@/auth/provider';
import { isStaff, type Session } from '@/auth/session';
import { Shell } from '@/components/shell';
import { Brand } from '@/components/brand';
import { ErrorNotice, Loading } from '@/components/feedback';
import LoginPage from '@/pages/login';
import AccountPage from '@/pages/account';

const StudentsPage = lazy(() => import('@/pages/students'));

function SessionRoutes() {
  const { session, state } = useSession();
  if (state.status === 'pending') {
    return <div className="session-gate"><Brand /><Loading>Checking your session…</Loading></div>;
  }
  if (state.status === 'error') {
    return (
      <main className="session-gate">
        <Brand /><h1>Let’s reconnect.</h1>
        <ErrorNotice message={state.message} retry={() => { void session.restore(); }} />
      </main>
    );
  }
  if (state.status === 'anonymous') {
    return (
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="*" element={<Navigate replace to="/login" />} />
      </Routes>
    );
  }
  const home = isStaff(state.account) ? '/students' : '/account';
  return (
    <Routes>
      <Route element={<Shell />}>
        <Route path="/students" element={isStaff(state.account) ? (
          <Suspense fallback={<Loading>Loading directory…</Loading>}><StudentsPage /></Suspense>
        ) : <Navigate replace to="/account" />} />
        <Route path="/account" element={<AccountPage />} />
        <Route path="*" element={<Navigate replace to={home} />} />
      </Route>
    </Routes>
  );
}

export function App({ session }: { session: Session }) {
  return (
    <SessionProvider session={session}><SessionRoutes /></SessionProvider>
  );
}
