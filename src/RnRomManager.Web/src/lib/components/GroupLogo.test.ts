import { describe, expect, it } from 'vitest';
import { fireEvent, render } from '@testing-library/svelte';
import GroupLogo from './GroupLogo.svelte';

const nintendo = 'c0a4942143e872cd1ae29fc759e04526de2e909ac1732734d38550a29c2e2516';
const shared = `/api/discovery/publishers/logo?key=${nintendo}&v=1`;

describe('GroupLogo (B16)', () => {
	it('shows the shared logo, falls through to the starter art, then to the text banner', async () => {
		const { container } = render(GroupLogo, {
			kind: 'publishers',
			item: { key: nintendo, name: 'Nintendo', logoUrl: shared }
		});
		const img = () => container.querySelector('img');

		expect(img()!.getAttribute('src')).toBe(shared);
		await fireEvent.error(img()!);
		expect(img()!.getAttribute('src')).toBe('/publishers/nintendo.svg');
		await fireEvent.error(img()!);
		expect(img()).toBeNull();
		expect(container.querySelector('.group-logo-banner')!.textContent).toBe('Nintendo');
	});

	it('is decorative: hidden from assistive tech, with an empty alt', () => {
		const { container } = render(GroupLogo, {
			kind: 'publishers',
			item: { key: nintendo, name: 'Nintendo', logoUrl: null }
		});

		expect(container.querySelector('.group-logo')!.getAttribute('aria-hidden')).toBe('true');
		expect(container.querySelector('img')!.getAttribute('alt')).toBe('');
	});

	it('a system without a shared logo is a banner of its name, at the given size', () => {
		const { container } = render(GroupLogo, {
			kind: 'systems',
			item: { key: 'gb', name: 'Game Boy', logoUrl: null },
			width: 120,
			height: 34
		});

		expect(container.querySelector('img')).toBeNull();
		expect(container.querySelector('.group-logo-banner')!.textContent).toBe('Game Boy');
		const box = container.querySelector<HTMLElement>('.group-logo')!;
		expect([box.style.width, box.style.height]).toEqual(['120px', '34px']);
	});

	it.each([
		['a shared logo', { key: 'gb', name: 'Game Boy', logoUrl: '/x.png' }],
		['the text banner', { key: 'zz', name: 'Unknown', logoUrl: null }]
	] as const)('%s sits in the same fixed frame, with no double border (B14)', (_, item) => {
		const { container } = render(GroupLogo, { kind: 'systems', item: { ...item } });
		const frame = container.querySelector<HTMLElement>('.group-logo')!;
		expect(frame.className).toContain('border rounded bg-body-tertiary');
		expect(frame.style.width).toBe('100px');
		expect(frame.style.height).toBe('28px');
		const banner = container.querySelector('.group-logo-banner');
		if (banner) expect(banner.classList).not.toContain('border');
	});
});
