import { NavLink, Outlet } from 'react-router-dom';
import { useSession } from '@/auth/provider';
import { isStaff, roleLabel } from '@/auth/session';
import { Brand } from './brand';
import { Button } from './ui/button';
import { ErrorNotice, Loading } from './feedback';

export function Shell() {
  const { session, state } = useSession();
  if (state.status !== 'authenticated') return null;
  const { account } = state;
  return (
    <div className="app-layout">
      <a className="skip-link" href="#main-content">Skip to content</a>
      <aside className="sidebar">
        <Brand />
        <div className="workspace-label">
          <span className="workspace-dot" />
          <span>{account.gymId === null ? 'Global workspace' : `Gym ${account.gymId}`}</span>
        </div>
        <nav aria-label="Main navigation">
          <p className="nav-caption">WORKSPACE</p>
          {isStaff(account) ? (
            <NavLink to="/students">
              <span aria-hidden="true">◫</span> Students
              <span className="nav-arrow" aria-hidden="true">↗</span>
            </NavLink>
          ) : null}
          <NavLink to="/account"><span aria-hidden="true">◎</span> Account</NavLink>
        </nav>
        <div className="sidebar-bottom">
          <span className="eyebrow">STAY IN YOUR ELEMENT.</span>
          <p>Show up. Build momentum.</p>
        </div>
      </aside>
      <div className="workspace">
        <header className="topbar">
          <span className="topbar-label">YOUR GYM. IN FOCUS.</span>
          <div className="session-info">
            <div>
              <span className="session-email">{account.email}</span>
              <span className="session-role">{roleLabel(account.role)}</span>
            </div>
            <Button variant="outline" onClick={() => { void session.logout(); }} disabled={state.busy}>
              {state.busy ? 'Signing out…' : 'Sign out'}
            </Button>
          </div>
        </header>
        <main id="main-content" tabIndex={-1}>
          {state.message ? <ErrorNotice message={state.message} /> : null}
          {state.busy ? <Loading>Updating your session…</Loading> : <Outlet />}
        </main>
        <footer className="workspace-footer"><span>Static-X</span><span>FOCUS ON THE WORK.</span></footer>
      </div>
    </div>
  );
}
