import type { ToastLink } from './ToastLink.js';

export interface ToastMessage {
	id: number;
	text: string;
	kind: 'info' | 'warning' | 'danger' | 'success';
	/** A link (an app path, already `resolve()`d); clicking it dismisses the toast (B8). */
	link?: ToastLink;
}
