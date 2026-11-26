/** `fn` called `ms` after the last call, with that call's arguments; `cancel()` drops a pending call. */
export function debounced<A extends unknown[]>(
	fn: (...args: A) => void,
	ms: number
): ((...args: A) => void) & { cancel(): void } {
	let timer: ReturnType<typeof setTimeout> | undefined;
	const call = (...args: A): void => {
		clearTimeout(timer);
		timer = setTimeout(() => fn(...args), ms);
	};
	return Object.assign(call, { cancel: () => clearTimeout(timer) });
}
