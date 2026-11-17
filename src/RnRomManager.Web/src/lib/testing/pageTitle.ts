import { expect } from 'vitest';

/** B1: the page has exactly one `<h1>`, the `PageTitle` one (hidden from `lg` up by `.page-title`), with this text. */
export function expectOnePageTitle(text: string): void {
	const headings = [...document.querySelectorAll('h1')];
	expect(headings).toHaveLength(1);
	expect(headings[0].classList.contains('page-title')).toBe(true);
	expect(headings[0].textContent?.trim()).toBe(text);
}
