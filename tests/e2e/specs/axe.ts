import AxeBuilder from '@axe-core/playwright';
import { expect, type Page } from '@playwright/test';

/**
 * Spec §11: axe on every page visited, failing on `serious` / `critical` (WCAG 2.x A + AA rules), in both themes
 * ("contrast checked in both themes"). The app's theme is Auto, so emulating `prefers-color-scheme` switches it live.
 */
/** @param exclude a selector axe skips, e.g. the emulator iframe, whose UI is EmulatorJS's own. */
export async function expectAccessible(page: Page, what: string, exclude?: string): Promise<void> {
	// A hover transition caught half-way reads as a contrast failure: park the mouse and freeze transitions first.
	await page.mouse.move(0, 0);
	await page.addStyleTag({ content: '*, *::before, *::after { transition: none !important; }' });
	// The navbar is sticky (B26): scrolled content passes under it, which axe's target-size rule reports as
	// "obscured". Scan from the top, where nothing sits under the navbar (modals are fixed, so unaffected).
	await page.evaluate(() => window.scrollTo(0, 0));

	const blocking: string[] = [];
	for (const colorScheme of ['light', 'dark'] as const) {
		await page.emulateMedia({ colorScheme });
		await expect(page.locator('html')).toHaveAttribute('data-bs-theme', colorScheme);
		const builder = new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']);
		const results = await (exclude ? builder.exclude(exclude) : builder).analyze();
		for (const v of results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical'))
			blocking.push(
				`[${colorScheme}] ${v.id} (${v.impact}): ${v.help} → ${v.nodes
					.map((n) => `${n.target.join(' ')} [${n.failureSummary?.replace(/\s+/g, ' ')}]`)
					.join(', ')}`
			);
	}
	await page.emulateMedia({ colorScheme: 'light' });
	expect(blocking, `axe on ${what}`).toEqual([]);
}
