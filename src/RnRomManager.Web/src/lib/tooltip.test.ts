import { describe, expect, it, vi } from 'vitest';

const created: {
	node: Element;
	options: unknown;
	setContent: ReturnType<typeof vi.fn>;
	dispose: ReturnType<typeof vi.fn>;
}[] = [];

vi.mock('./bootstrap', () => ({
	Tooltip: class {
		setContent = vi.fn();
		dispose = vi.fn();
		constructor(node: Element, options: unknown) {
			created.push({ node, options, setContent: this.setContent, dispose: this.dispose });
		}
	}
}));

const { tooltip } = await import('./tooltip');

describe('tooltip action', () => {
	it('creates on mount with the text, updates on change, disposes on destroy', () => {
		const node = document.createElement('a');

		const action = tooltip(node, 'Legend of Zelda');

		expect(created).toHaveLength(1);
		expect(created[0].node).toBe(node);
		expect(created[0].options).toEqual({
			title: 'Legend of Zelda',
			placement: 'top',
			trigger: 'hover focus'
		});

		action!.update!('Tetris');
		expect(created[0].setContent).toHaveBeenCalledWith({ '.tooltip-inner': 'Tetris' });

		action!.destroy!();
		expect(created[0].dispose).toHaveBeenCalledOnce();
	});
});
