import { afterEach, describe, expect, it, vi } from 'vitest';
import { scrollActiveTab } from './scrollActiveTab';

/** jsdom has no layout: each element's box is given by hand. */
function box(el: Element, left: number, width: number): void {
	el.getBoundingClientRect = () => ({ left, right: left + width, width }) as DOMRect;
}

describe('scrollActiveTab (U8)', () => {
	afterEach(() => (document.body.innerHTML = ''));

	function setup(activeIndex: number) {
		document.body.innerHTML = `<ul class="nav nav-tabs">${[0, 1, 2, 3]
			.map((i) => `<li><a class="nav-link${i === activeIndex ? ' active' : ''}">Tab ${i}</a></li>`)
			.join('')}</ul>`;
		const list = document.querySelector('ul')!;
		const links = [...list.querySelectorAll('a')];
		// A 200px list scrolled to `scrollLeft`; tabs are 100px wide, laid out from 0.
		const layout = () => {
			box(list, 0, 200);
			links.forEach((a, i) => box(a, i * 100 - list.scrollLeft, 100));
		};
		let scrollLeft = 0;
		Object.defineProperty(list, 'scrollLeft', {
			configurable: true,
			get: () => scrollLeft,
			set: (v: number) => {
				scrollLeft = v;
				layout();
			}
		});
		layout();
		const scrollIntoView = vi.fn();
		links.forEach((a) => (a.scrollIntoView = scrollIntoView));
		return { list, links, scrollIntoView };
	}

	it('scrolls an off-screen active tab fully into view on mount', () => {
		const { list, scrollIntoView } = setup(3);
		const action = scrollActiveTab(list);

		expect(list.scrollLeft).toBe(200);
		expect(scrollIntoView).not.toHaveBeenCalled();
		action?.destroy?.();
	});

	it('leaves a visible active tab alone', () => {
		const { list } = setup(1);
		scrollActiveTab(list);
		expect(list.scrollLeft).toBe(0);
	});

	it('follows the active tab when it changes, both ways, never calling scrollIntoView', async () => {
		const { list, links, scrollIntoView } = setup(0);
		const action = scrollActiveTab(list);

		links[0].classList.remove('active');
		links[2].classList.add('active');
		await vi.waitFor(() => expect(list.scrollLeft).toBe(100));

		links[2].classList.remove('active');
		links[0].classList.add('active');
		await vi.waitFor(() => expect(list.scrollLeft).toBe(0));
		expect(scrollIntoView).not.toHaveBeenCalled();
		action?.destroy?.();
	});

	it('scrolls sideways on a vertical wheel, letting the page scroll once the strip hits an end', () => {
		const { list } = setup(0);
		const action = scrollActiveTab(list);
		// Like a real element, scrollLeft clamps to 0..(scrollWidth - clientWidth) = 0..200.
		let pos = 0;
		Object.defineProperty(list, 'scrollLeft', {
			get: () => pos,
			set: (v: number) => (pos = Math.max(0, Math.min(200, v)))
		});
		const spin = (deltaY: number) => {
			const e = new WheelEvent('wheel', { deltaY, cancelable: true });
			list.dispatchEvent(e);
			return e.defaultPrevented;
		};

		expect(spin(150)).toBe(true);
		expect(list.scrollLeft).toBe(150);
		expect(spin(150)).toBe(true);
		expect(list.scrollLeft).toBe(200);
		expect(spin(100)).toBe(false); // already at the end: page scrolls
		expect(spin(-300)).toBe(true);
		expect(list.scrollLeft).toBe(0);
		action?.destroy?.();
	});
});
