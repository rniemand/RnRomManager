import AxeBuilder from '@axe-core/playwright';
import { expect, type Page } from '@playwright/test';

/** Spec §11: axe on every page visited; fail on `serious` / `critical` (WCAG 2.x A + AA rules). */
export async function expectAccessible(page: Page, what: string): Promise<void> {
	// A hover transition caught half-way reads as a contrast failure: park the mouse and freeze transitions first.
	await page.mouse.move(0, 0);
	await page.addStyleTag({ content: '*, *::before, *::after { transition: none !important; }' });
	const results = await new AxeBuilder({ page })
		.withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
		.analyze();
	const blocking = results.violations
		.filter((v) => v.impact === 'serious' || v.impact === 'critical')
		.map(
			(v) =>
				`${v.id} (${v.impact}): ${v.help} → ${v.nodes
					.map((n) => `${n.target.join(' ')} [${n.failureSummary?.replace(/\s+/g, ' ')}]`)
					.join(', ')}`
		);
	expect(blocking, `axe on ${what}`).toEqual([]);
}
