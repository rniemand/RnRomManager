// The UMD bundle (Bootstrap + Popper) has no type declarations; only what the app uses is declared.
declare module 'bootstrap/dist/js/bootstrap.bundle.min.js' {
	export class Modal {
		constructor(
			element: Element,
			options?: { backdrop?: boolean | 'static'; keyboard?: boolean; focus?: boolean }
		);
		show(): void;
		hide(): void;
		dispose(): void;
	}
	const bootstrap: { Modal: typeof Modal };
	export default bootstrap;
}
