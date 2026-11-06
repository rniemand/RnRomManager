/** A laid-out treemap tile: `item` scaled to the box, in the box's units. */
export interface TreemapRect<T> {
	x: number;
	y: number;
	w: number;
	h: number;
	item: T;
}

const sum = (values: number[]) => values.reduce((a, b) => a + b, 0);

/** The worst aspect ratio of a row of `areas` laid along a side of length `side` (Bruls et al.). */
function worst(areas: number[], side: number): number {
	const s = sum(areas);
	return Math.max(
		(side * side * Math.max(...areas)) / (s * s),
		(s * s) / (side * side * Math.min(...areas))
	);
}

/**
 * RP3: squarified treemap. Each tile's area is proportional to its `value`; zero and negative values are dropped;
 * the tiles cover the `width` × `height` box exactly (the last tile of each row, and the last row, take what is left,
 * so rounding never leaves a gap).
 */
export function squarify<T extends { value: number }>(
	items: readonly T[],
	width: number,
	height: number
): TreemapRect<T>[] {
	const data = items.filter((i) => i.value > 0).sort((a, b) => b.value - a.value);
	const total = sum(data.map((d) => d.value));
	if (!data.length || width <= 0 || height <= 0) return [];
	const scale = (width * height) / total;
	let rest = data.map((item) => ({ item, area: item.value * scale }));
	const out: TreemapRect<T>[] = [];
	let x = 0;
	let y = 0;
	let w = width;
	let h = height;

	while (rest.length) {
		const side = Math.min(w, h);
		let k = 1;
		while (
			k < rest.length &&
			worst(
				rest.slice(0, k + 1).map((r) => r.area),
				side
			) <=
				worst(
					rest.slice(0, k).map((r) => r.area),
					side
				)
		)
			k++;
		const row = rest.slice(0, k);
		rest = rest.slice(k);
		const rowArea = sum(row.map((r) => r.area));

		if (w >= h) {
			// A column on the left, as tall as the box.
			const thick = rest.length ? rowArea / h : w;
			let cy = y;
			row.forEach((r, i) => {
				const rh = i === row.length - 1 ? y + h - cy : r.area / thick;
				out.push({ x, y: cy, w: thick, h: rh, item: r.item });
				cy += rh;
			});
			x += thick;
			w -= thick;
		} else {
			// A row along the top, as wide as the box.
			const thick = rest.length ? rowArea / w : h;
			let cx = x;
			row.forEach((r, i) => {
				const rw = i === row.length - 1 ? x + w - cx : r.area / thick;
				out.push({ x: cx, y, w: rw, h: thick, item: r.item });
				cx += rw;
			});
			y += thick;
			h -= thick;
		}
	}
	return out;
}
