import type { Action } from 'svelte/action';
import { Tooltip } from './bootstrap';

/** `use:tooltip={text}`: a Bootstrap tooltip on top, shown on hover and focus (spec B25). The one place tooltips are created. */
export const tooltip: Action<HTMLElement, string> = (node, text) => {
	const instance = new Tooltip(node, { title: text, placement: 'top', trigger: 'hover focus' });
	return {
		update(next: string) {
			instance.setContent({ '.tooltip-inner': next });
		},
		destroy() {
			instance.dispose();
		}
	};
};
