import type { Action } from 'svelte/action';

/**
 * U8: `use:scrollActiveTab` on a `ul.nav-tabs` keeps its `.active` tab fully visible, on mount and whenever the active
 * tab changes, by setting only the list's `scrollLeft` (never `scrollIntoView`, which would scroll the page too).
 * It also maps the vertical mouse wheel to horizontal scrolling of the strip.
 */
export const scrollActiveTab: Action<HTMLElement> = (list) => {
	const reveal = () => {
		const active = list.querySelector<HTMLElement>('.active');
		if (!active) return;
		const outer = list.getBoundingClientRect();
		const inner = active.getBoundingClientRect();
		if (inner.left < outer.left) list.scrollLeft += inner.left - outer.left;
		else if (inner.right > outer.right) list.scrollLeft += inner.right - outer.right;
	};
	reveal();
	const observer = new MutationObserver(reveal);
	observer.observe(list, {
		subtree: true,
		attributes: true,
		attributeFilter: ['class'],
		childList: true
	});
	// A vertical mouse wheel scrolls an overflowing tab strip sideways; at either end the page scrolls as usual.
	const wheel = (e: WheelEvent) => {
		if (e.ctrlKey || Math.abs(e.deltaX) >= Math.abs(e.deltaY)) return;
		const before = list.scrollLeft;
		list.scrollLeft += e.deltaMode === WheelEvent.DOM_DELTA_LINE ? e.deltaY * 16 : e.deltaY;
		if (list.scrollLeft !== before) e.preventDefault();
	};
	list.addEventListener('wheel', wheel, { passive: false });
	return {
		destroy: () => {
			observer.disconnect();
			list.removeEventListener('wheel', wheel);
		}
	};
};
