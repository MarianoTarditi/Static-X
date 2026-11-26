import { useState, type FormEvent } from 'react';
import { useSession } from '@/auth/provider';
import { Brand } from '@/components/brand';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { ErrorNotice } from '@/components/feedback';

export default function LoginPage() {
  const { session, state } = useSession();
  const [scope, setScope] = useState('gym');
  const [validation, setValidation] = useState('');
  const busy = state.status === 'anonymous' && state.busy;

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    const form = event.currentTarget;
    const fields = new FormData(form);
    const gymId = scope === 'global' ? null : Number(fields.get('gymId'));
    if (gymId !== null && (!Number.isSafeInteger(gymId) || gymId < 1 || gymId > 2147483647)) {
      setValidation('Enter the positive gym ID provided by your administrator.');
      return;
    }
    setValidation('');
    const password = String(fields.get('password'));
    // Do not retain credentials in state, browser storage or the DOM after submission.
    form.reset();
    void session.login({ gymId, email: String(fields.get('email')).trim(), password });
  }

  return <main className="login-layout">
    <section className="login-story" aria-label="Static-X workspace">
      <Brand />
      <div className="story-copy"><p className="eyebrow">THE WORK STARTS HERE</p><h1>Built for<br />the everyday<br /><span>commitment.</span></h1><p>Less friction. More focus.<br />Your gym, connected.</p></div>
      <img className="hero-mark" src="/brand/static-x-mark.svg" alt="" width="685" height="440" />
      <div className="story-footer"><span>STATIC-X / GYM WORKSPACE</span><span aria-hidden="true">01 — ACCESS</span></div>
    </section>
    <section className="login-form-panel" aria-labelledby="login-heading">
      <div className="mobile-brand"><Brand /></div>
      <div className="login-form-wrap"><p className="eyebrow">YOUR NEXT SESSION</p><h2 id="login-heading">Welcome back.</h2><p className="muted intro">Sign in to your Static-X workspace.</p>
        <form onSubmit={submit} className="login-form">
          <fieldset disabled={busy}><legend className="sr-only">Sign-in details</legend>
            <label htmlFor="scope">Account type</label>
            <select id="scope" value={scope} onChange={(event) => setScope(event.target.value)}>
              <option value="gym">Gym account</option><option value="global">Global administrator</option>
            </select>
            <p className="field-hint" id="scope-help">{scope === 'gym' ? 'Use the gym ID provided by your administrator.' : 'For global SuperAdmin accounts only. No gym will be selected.'}</p>
            {scope === 'gym' ? <div className="field"><label htmlFor="gymId">Gym ID</label><Input id="gymId" name="gymId" type="number" min="1" max="2147483647" step="1" required aria-describedby="scope-help" inputMode="numeric" placeholder="Your gym ID" /></div> : null}
            <div className="field"><label htmlFor="email">Email</label><Input id="email" name="email" type="email" autoComplete="username" required placeholder="you@example.com" /></div>
            <div className="field"><label htmlFor="password">Password</label><Input id="password" name="password" type="password" autoComplete="current-password" required /></div>
            <Button type="submit" className="w-full mt-3">{busy ? 'Signing in…' : 'Sign in'}<span aria-hidden="true">↗</span></Button>
          </fieldset>
        </form>
        {validation || (state.status === 'anonymous' && state.message) ? <ErrorNotice message={validation || (state.status === 'anonymous' ? state.message! : '')} /> : null}
        <p className="login-help">Need access? Contact your gym administrator.</p>
      </div>
      <p className="security-note"><span aria-hidden="true">◇</span> Secure session. Credentials stay private.</p>
    </section>
  </main>;
}
