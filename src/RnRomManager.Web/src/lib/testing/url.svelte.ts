/**
 * A reactive stand-in for `page.url` in route tests: mock `$app/state` with a getter returning `testUrl.current`
 * and let the mocked `goto` call `navigateTo`, so `$derived(page.url…)` re-runs like it does in the app.
 */
/* eslint-disable svelte/prefer-svelte-reactivity -- each navigation replaces the URL; it is never mutated */
export const testUrl = $state({ current: new URL('http://localhost/') });

export function navigateTo(path: string): void {
	testUrl.current = new URL(path, 'http://localhost');
}
