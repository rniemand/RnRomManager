import type { Action } from 'svelte/action';

/**
 * U8: `use:scrollActiveTab` on a `ul.nav-tabs` keeps its `.active` tab fully visible, on mount and whenever the active
 * tab changes, by setting only the list's `scrollLeft` (never `scrollIntoView`, which would scroll the page too).
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
	return { destroy: () => observer.disconnect() };
};
