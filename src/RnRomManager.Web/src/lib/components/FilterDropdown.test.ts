import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/svelte';
import '#lib/bootstrap.js'; // the real data API: open/close and keyboard come from Bootstrap
import FilterDropdown, { type FilterOption } from './FilterDropdown.svelte';

const systems: FilterOption[] = [
	{ value: 's32x', label: 'Sega 32X', badge: { system: 's32x', name: '32X' }, search: ['32X'] },
	{ value: 'gb', label: 'Game Boy', badge: { system: 'gb', name: 'GB' }, search: ['GB'] },
	{ value: 'gbc', label: 'Game Boy Color', badge: { system: 'gbc', name: 'GBC' }, search: ['GBC'] }
];

type Props = Parameters<typeof FilterDropdown>[1];

function setup(props: Partial<Props> = {}) {
	const onselect = vi.fn();
	render(FilterDropdown, {
		label: 'System',
		options: systems,
		value: null,
		counts: { s32x: 1250, gb: 12, gbc: 0 },
		all: { label: 'All systems', sum: true },
		filterPlaceholder: 'Filter systems…',
		onselect,
		...props
	});
	const toggle = document.querySelector('.dropdown-toggle') as HTMLButtonElement;
	const rows = () =>
		[...document.querySelectorAll('.dropdown-item')].map((b) => ({
			text: [...b.children].map((c) => c.textContent?.trim()).join(' | '),
			button: b as HTMLButtonElement
		}));
	return { onselect, toggle, rows };
}

async function open(toggle: HTMLButtonElement): Promise<void> {
	await fireEvent.click(toggle);
	await vi.waitFor(() => expect(toggle.getAttribute('aria-expanded')).toBe('true'));
}

describe('FilterDropdown (L1–L5)', () => {
	afterEach(() => cleanup());

	it('toggle reads the field alone and is outlined when nothing is chosen', () => {
		const { toggle } = setup();

		expect(toggle.textContent?.trim()).toBe('System');
		expect(toggle.getAttribute('aria-label')).toBe('System: All');
		expect(toggle.className).toContain('btn-outline-secondary');
		expect(toggle.className).toContain('btn-sm');
	});

	it('toggle shows the chosen value and is filled', () => {
		const { toggle } = setup({ value: 'gb' });

		expect(toggle.textContent?.trim()).toBe('System: Game Boy');
		expect(toggle.getAttribute('aria-label')).toBe('System: Game Boy');
		expect(toggle.className).toMatch(/\bbtn-secondary\b/);
	});

	it('active overrides the filled state (Sort at its default)', () => {
		const { toggle } = setup({
			value: 'title',
			active: false,
			options: [{ value: 'title', label: 'Title' }]
		});

		expect(toggle.textContent?.trim()).toBe('System: Title');
		expect(toggle.className).toContain('btn-outline-secondary');
	});

	it('All row first with the summed count, then badge + name rows with right-aligned formatted counts', () => {
		const { rows } = setup();

		expect(rows().map((r) => r.text)).toEqual([
			'All systems | 1,262',
			'32XSega 32X | 1,250',
			'GBGame Boy | 12',
			'GBCGame Boy Color | 0'
		]);
		const count = rows()[1].button.lastElementChild!;
		expect(count.className).toBe('ms-auto text-body-secondary font-monospace');
		expect(rows()[1].button.querySelector('.badge')?.textContent).toBe('32X');
	});

	it('the All row has no count when sum is off (Region)', () => {
		const { rows } = setup({ all: { label: 'All regions', sum: false } });

		expect(rows()[0].text).toBe('All regions');
	});

	it.each([null, undefined])('no counts at all while counts are %s', (counts) => {
		const { rows } = setup({ counts });

		expect(rows().map((r) => r.text)).toEqual([
			'All systems',
			'32XSega 32X',
			'GBGame Boy',
			'GBCGame Boy Color'
		]);
	});

	it('an option missing from the counts counts 0; zero rows stay listed, clickable and muted', async () => {
		const { rows, onselect } = setup({ counts: { s32x: 1, gb: 2 } });
		const zero = rows()[3];

		expect(zero.text).toBe('GBCGame Boy Color | 0');
		expect(zero.button.classList.contains('text-body-secondary')).toBe(true);
		expect(rows()[2].button.classList.contains('text-body-secondary')).toBe(false);
		await fireEvent.click(zero.button);
		expect(onselect).toHaveBeenCalledWith('gbc');
	});

	it('marks the active row with active and aria-current', () => {
		const { rows } = setup({ value: 'gb' });

		const active = rows().filter((r) => r.button.classList.contains('active'));
		expect(active.map((r) => r.text)).toEqual(['GBGame Boy | 12']);
		expect(active[0].button.getAttribute('aria-current')).toBe('true');
		expect(rows()[0].button.hasAttribute('aria-current')).toBe(false);
	});

	it('the All row is active when nothing is chosen', () => {
		const { rows } = setup();

		expect(rows()[0].button.getAttribute('aria-current')).toBe('true');
	});

	it('opening focuses the filter input, which filters on name, id and short name, ignoring case', async () => {
		const { toggle, rows } = setup();
		await open(toggle);
		const input = screen.getByPlaceholderText('Filter systems…') as HTMLInputElement;

		await vi.waitFor(() => expect(document.activeElement).toBe(input));
		expect(input.className).toBe('form-control form-control-sm');

		await fireEvent.input(input, { target: { value: 'COLOR' } });
		expect(rows().map((r) => r.text)).toEqual(['All systems | 1,262', 'GBCGame Boy Color | 0']);
		await fireEvent.input(input, { target: { value: 's32' } });
		expect(
			rows()
				.slice(1)
				.map((r) => r.text)
		).toEqual(['32XSega 32X | 1,250']);
		await fireEvent.input(input, { target: { value: 'gbc' } });
		expect(
			rows()
				.slice(1)
				.map((r) => r.text)
		).toEqual(['GBCGame Boy Color | 0']);
	});

	it('a menu without a filter placeholder has no filter input', () => {
		setup({ filterPlaceholder: undefined });

		expect(document.querySelector('input')).toBeNull();
	});

	it('Escape in the filter closes the menu', async () => {
		const { toggle } = setup();
		await open(toggle);
		const input = screen.getByPlaceholderText('Filter systems…');
		await vi.waitFor(() => expect(document.activeElement).toBe(input));

		await fireEvent.keyDown(input, { key: 'Escape' });

		await vi.waitFor(() => expect(toggle.getAttribute('aria-expanded')).toBe('false'));
		expect(document.activeElement).toBe(toggle);
	});

	// Arrow keys are Bootstrap's own (data API); jsdom has no layout, so its visibility check can't be run here.
	it('the toggle uses the Bootstrap dropdown data API, so its keyboard support applies', () => {
		const { toggle } = setup();

		expect(toggle.getAttribute('data-bs-toggle')).toBe('dropdown');
		expect(toggle.getAttribute('aria-expanded')).toBe('false');
	});

	it('choosing calls back, closes the menu and returns focus to the toggle', async () => {
		const { toggle, rows, onselect } = setup({ value: 'gb' });
		await open(toggle);

		await fireEvent.click(rows()[0].button);

		expect(onselect).toHaveBeenCalledWith(null);
		await vi.waitFor(() => expect(toggle.getAttribute('aria-expanded')).toBe('false'));
		expect(document.activeElement).toBe(toggle);
	});

	it('the menu has the shared width and scroll limits', () => {
		setup();

		expect(document.querySelector('ul.dropdown-menu')!.classList.contains('filter-menu')).toBe(
			true
		);
	});
});
