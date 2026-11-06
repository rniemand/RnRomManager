import { describe, expect, it } from 'vitest';
import { squarify, type TreemapRect } from './treemap';

const items = (...values: number[]) => values.map((value, i) => ({ id: i, value }));

function overlaps(a: TreemapRect<unknown>, b: TreemapRect<unknown>): boolean {
	const e = 1e-6;
	return a.x + e < b.x + b.w && b.x + e < a.x + a.w && a.y + e < b.y + b.h && b.y + e < a.y + a.h;
}

describe('squarify', () => {
	it.each([
		[[6, 6, 4, 3, 2, 2, 1], 600, 400],
		[[100, 1, 1, 1], 300, 900],
		[[5, 5, 5, 5, 5, 5, 5, 5, 5], 1000, 500],
		[Array.from({ length: 301 }, (_, i) => 1000 - i * 3), 1000, 500]
	])('tiles the box exactly, without overlap, inside the bounds (%#)', (values, width, height) => {
		const rects = squarify(items(...values), width, height);

		expect(rects).toHaveLength(values.length);
		const area = rects.reduce((s, r) => s + r.w * r.h, 0);
		expect(Math.abs(area - width * height)).toBeLessThan(0.5);
		for (const r of rects) {
			expect(r.x).toBeGreaterThanOrEqual(-1e-6);
			expect(r.y).toBeGreaterThanOrEqual(-1e-6);
			expect(r.x + r.w).toBeLessThanOrEqual(width + 1e-6);
			expect(r.y + r.h).toBeLessThanOrEqual(height + 1e-6);
		}
		for (let i = 0; i < rects.length; i++)
			for (let j = i + 1; j < rects.length; j++) expect(overlaps(rects[i], rects[j])).toBe(false);
	});

	it('makes each area proportional to its value', () => {
		const rects = squarify(items(6, 6, 4, 3, 2, 2, 1), 600, 400);
		const perUnit = (600 * 400) / 24;

		for (const r of rects) expect(r.w * r.h).toBeCloseTo(r.item.value * perUnit, 6);
	});

	it('drops zero values', () => {
		const rects = squarify(items(3, 0, 1), 100, 100);

		expect(rects.map((r) => r.item.value)).toEqual([3, 1]);
	});

	it('a single item fills the box; nothing to draw gives no tiles', () => {
		expect(squarify(items(7), 120, 80)).toEqual([
			{ x: 0, y: 0, w: 120, h: 80, item: { id: 0, value: 7 } }
		]);
		expect(squarify(items(0), 120, 80)).toEqual([]);
		expect(squarify([], 120, 80)).toEqual([]);
	});
});
