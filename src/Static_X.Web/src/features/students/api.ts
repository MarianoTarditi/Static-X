export interface Student { id: number; nombre: string; apellido: string; email: string; activo: boolean }
export interface StudentPage { items: Student[]; page: number; pageSize: number; totalCount: number }
export interface StudentFilters { page: number; search: string; state: string }

export function studentsPath(filters: StudentFilters) {
  const params = new URLSearchParams({ page: String(filters.page), pageSize: '20' });
  if (filters.search) params.set('search', filters.search);
  if (filters.state === 'true' || filters.state === 'false') params.set('activo', filters.state);
  return `/students?${params}`;
}

export function parseStudentPage(value: unknown): StudentPage {
  if (!value || typeof value !== 'object') throw new Error('Invalid student page');
  const p = value as StudentPage;
  if (!Array.isArray(p.items) || !Number.isSafeInteger(p.page) || p.page < 1 ||
    !Number.isSafeInteger(p.pageSize) || p.pageSize < 1 || p.pageSize > 100 ||
    !Number.isSafeInteger(p.totalCount) || p.totalCount < 0 || p.items.length > p.pageSize ||
    !p.items.every((s) => s && Number.isSafeInteger(s.id) && s.id > 0 && typeof s.nombre === 'string' &&
      typeof s.apellido === 'string' && typeof s.email === 'string' && typeof s.activo === 'boolean')) {
    throw new Error('Invalid student page');
  }
  return p;
}
