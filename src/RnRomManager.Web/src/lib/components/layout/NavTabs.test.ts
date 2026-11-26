import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import NavTabs from './NavTabs.svelte';

describe('NavTabs', () => {
	it('link mode renders anchors with aria-current on the active tab', () => {
		const { container } = render(NavTabs, {
			props: {
				tabs: [
					{ id: 'a', label: 'Alpha', href: '/a', icon: 'admin-users' },
					{ id: 'b', label: 'Beta', href: '/b' }
				],
				active: 'b',
				class: 'mb-3',
				linkClass: 'd-flex align-items-center gap-1'
			}
		});

		const ul = container.querySelector('ul')!;
		expect(ul.className).toBe('nav nav-tabs mb-3');
		expect(ul.hasAttribute('role')).toBe(false);
		expect(container.querySelector('li')!.className).toBe('nav-item');
		expect(container.querySelector('li')!.hasAttribute('role')).toBe(false);
		const links = screen.getAllByRole('link');
		expect(links.map((a) => [a.getAttribute('href'), a.getAttribute('aria-current')])).toEqual([
			['/a', null],
			['/b', 'page']
		]);
		expect(links[0].className).toBe('nav-link d-flex align-items-center gap-1');
		expect(links[1].className).toBe('nav-link d-flex align-items-center gap-1 active');
		expect(links[0].firstElementChild!.tagName).toBe('I');
		expect(links[0].textContent).toBe(' Alpha');
		expect(links[1].textContent).toBe('Beta');
	});

	it('button mode renders a tablist with ids, aria-controls and aria-selected', async () => {
		const onselect = vi.fn();
		const { container } = render(NavTabs, {
			props: {
				tabs: [
					{ id: 'x', label: 'X', tabId: 'tab-x', controls: 'panel-x' },
					{ id: 'y', label: 'Y', tabId: 'tab-y', controls: 'panel-y', title: 'Why' }
				],
				active: 'x',
				onselect
			}
		});

		expect(container.querySelector('ul')!.className).toBe('nav nav-tabs');
		expect(screen.getByRole('tablist')).toBeTruthy();
		expect(container.querySelector('li')!.getAttribute('role')).toBe('presentation');
		const tabs = screen.getAllByRole('tab');
		expect(
			tabs.map((b) => [b.id, b.getAttribute('aria-controls'), b.getAttribute('aria-selected')])
		).toEqual([
			['tab-x', 'panel-x', 'true'],
			['tab-y', 'panel-y', 'false']
		]);
		expect(tabs[0].className).toBe('nav-link active');
		expect(tabs[1].getAttribute('type')).toBe('button');
		expect(tabs[1].title).toBe('Why');
		await fireEvent.click(tabs[1]);
		expect(onselect).toHaveBeenCalledWith('y');
	});
});
