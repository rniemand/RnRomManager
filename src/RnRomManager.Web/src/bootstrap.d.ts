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
	export class Tooltip {
		constructor(
			element: Element,
			options?: {
				title?: string;
				placement?: 'top' | 'bottom' | 'left' | 'right';
				trigger?: string;
			}
		);
		setContent(content: Record<string, string>): void;
		dispose(): void;
	}
	export class Dropdown {
		static getOrCreateInstance(element: Element): Dropdown;
		hide(): void;
	}
	const bootstrap: { Modal: typeof Modal; Tooltip: typeof Tooltip; Dropdown: typeof Dropdown };
	export default bootstrap;
}
