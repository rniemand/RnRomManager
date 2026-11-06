/**
 * G1: Bootstrap's `dispose()` doesn't undo what `show()` did to `<body>`, so a modal unmounted while shown or still
 * fading out leaves the page unscrollable. Call this in `onDestroy` before `dispose()` (which drops the backdrop).
 */
export function releaseModal(element: Element | undefined | null): void {
	const body = document.body;
	const touched =
		!!element?.classList.contains('show') ||
		!!document.querySelector('.modal-backdrop') ||
		body.classList.contains('modal-open');
	if (!touched) return;
	// Another modal still open keeps its body state.
	if ([...document.querySelectorAll('.modal.show')].some((m) => m !== element)) return;
	body.classList.remove('modal-open');
	body.style.removeProperty('overflow');
	body.style.removeProperty('padding-right');
	document.querySelectorAll('.modal-backdrop').forEach((b) => b.remove());
}
