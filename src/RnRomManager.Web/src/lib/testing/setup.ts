import { vi } from 'vitest';

/** The `$app/paths` mock most tests share; a test file's own `vi.mock('$app/paths', …)` overrides it. */
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
