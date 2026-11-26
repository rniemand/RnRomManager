/** A laid-out treemap tile: `item` scaled to the box, in the box's units. */
export interface TreemapRect<T> {
	x: number;
	y: number;
	w: number;
	h: number;
	item: T;
}
