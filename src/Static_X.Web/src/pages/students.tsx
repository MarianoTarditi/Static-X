import { useState, type FormEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useSession } from '@/auth/provider';
import { isStaff } from '@/auth/session';
import { parseStudentPage, studentsPath, type StudentFilters } from '@/features/students/api';
import { safeError } from '@/lib/api';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { ErrorNotice, Loading } from '@/components/feedback';

export default function StudentsPage() {
  const { session, state } = useSession();
  const [draft, setDraft] = useState('');
  const [filters, setFilters] = useState<StudentFilters>({ page: 1, search: '', state: '' });
  const account = state.status === 'authenticated' ? state.account : null;
  const allowed = !!account && isStaff(account) && state.status === 'authenticated' && !state.busy;
  const query = useQuery({
    queryKey: ['students', account?.id, account?.gymId, filters],
    enabled: allowed,
    queryFn: async ({ signal }) => parseStudentPage(await session.privateGet(studentsPath(filters), signal)),
  });
  if (!allowed) return null;
  const data = query.data;
  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;

  function search(event: FormEvent) {
    event.preventDefault();
    setFilters((previous) => ({ ...previous, search: draft.trim(), page: 1 }));
  }

  return (
    <section className="page-content" aria-labelledby="students-title">
      <div className="page-heading">
        <div>
          <p className="eyebrow">PEOPLE / DIRECTORY</p>
          <h1 id="students-title">Students<span className="heading-dot">.</span></h1>
          <p className="page-description">The people behind the progress. Your gym’s student directory.</p>
        </div>
        <span className="readonly-label">READ-ONLY ACCESS</span>
      </div>
      <div className="directory-panel">
        <div className="directory-toolbar">
          <form className="search-form" onSubmit={search}>
            <label className="sr-only" htmlFor="search">Search students</label>
            <Input
              id="search" value={draft} maxLength={320} type="search"
              onChange={(event) => setDraft(event.target.value)}
              placeholder="Search by name or email"
            />
            <Button variant="outline" type="submit">Search</Button>
          </form>
          <div className="state-filter">
            <label htmlFor="student-state">Student state</label>
            <select
              id="student-state" value={filters.state}
              onChange={(event) => {
                const value = event.target.value;
                setFilters((previous) => ({ ...previous, state: value, page: 1 }));
              }}
            >
              <option value="">All states</option>
              <option value="true">Active</option>
              <option value="false">Inactive</option>
            </select>
          </div>
        </div>
        <div className="directory-summary">
          <span>{filters.search ? `Results for “${filters.search}”` : 'Student directory'}</span>
          <span>{query.isFetching ? 'Updating…' : data && !query.isError
            ? `${data.totalCount} ${data.totalCount === 1 ? 'student' : 'students'}` : '—'}</span>
        </div>
        {query.isPending ? (
          <Loading>Loading students…</Loading>
        ) : query.isError ? (
          <ErrorNotice
            message={safeError(query.error, 'Unable to load students. Check your connection and try again.')}
            retry={() => { void query.refetch(); }}
          />
        ) : data && data.items.length === 0 ? (
          <div className="empty-state">
            <span aria-hidden="true">↗</span>
            <h2>No students found</h2>
            <p>{filters.search || filters.state
              ? 'Try a different search or student state.'
              : 'Students registered with your gym will appear here.'}</p>
          </div>
        ) : data ? (
          <div className="table-scroll" role="region" aria-label="Student results" tabIndex={0}>
            <table>
              <caption className="sr-only">Students in your gym</caption>
              <thead><tr><th scope="col">Student</th><th scope="col">Email</th><th scope="col">State</th></tr></thead>
              <tbody>
                {data.items.map((student) => (
                  <tr key={student.id}>
                    <th scope="row">
                      <span className="student-avatar" aria-hidden="true">
                        {student.nombre.charAt(0)}{student.apellido.charAt(0)}
                      </span>
                      <span>{student.nombre} {student.apellido}</span>
                    </th>
                    <td>{student.email}</td>
                    <td>
                      <span className={`state-badge ${student.activo ? 'is-active' : ''}`}>
                        <span aria-hidden="true" />{student.activo ? 'Active' : 'Inactive'}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
        <div className="pagination">
          <p aria-live="polite">{data && !query.isError ? `Page ${data.page} of ${totalPages}` : `Page ${filters.page}`}</p>
          <div>
            <Button
              variant="outline" aria-label="Previous page"
              disabled={query.isFetching || filters.page <= 1}
              onClick={() => setFilters((previous) => ({ ...previous, page: previous.page - 1 }))}
            >←<span>Previous</span></Button>
            <Button
              variant="outline" aria-label="Next page"
              disabled={query.isFetching || query.isError || !data || filters.page >= totalPages}
              onClick={() => setFilters((previous) => ({ ...previous, page: previous.page + 1 }))}
            ><span>Next</span>→</Button>
          </div>
        </div>
      </div>
      <p className="directory-footnote">Only students in your current gym are shown. Account access and student state are managed separately.</p>
    </section>
  );
}
