import { useSession } from '@/auth/provider';
import { roleLabel } from '@/auth/session';

export default function AccountPage() {
  const { state } = useSession();
  if (state.status !== 'authenticated') return null;
  const { account } = state;
  return <section className="page-content"><p className="eyebrow">CONNECTED TO STATIC-X</p><h1>Your account</h1><p className="page-description">You are signed in. This first release provides the staff student directory; additional account tools are not available yet.</p>
    <dl className="account-details"><div><dt>Email</dt><dd>{account.email}</dd></div><div><dt>Access</dt><dd>{roleLabel(account.role)}</dd></div><div><dt>Workspace</dt><dd>{account.gymId === null ? 'Global account' : `Gym ${account.gymId}`}</dd></div></dl>
  </section>;
}
