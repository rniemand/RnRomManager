import { afterEach, describe, expect, it } from 'vitest';
import { releaseModal } from './modal';

function modalElement(shown: boolean): HTMLDivElement {
	const el = document.createElement('div');
	el.className = shown ? 'modal fade show' : 'modal fade';
	document.body.append(el);
	return el;
}

function lockBody(): void {
	document.body.classList.add('modal-open');
	document.body.style.overflow = 'hidden';
	document.body.style.paddingRight = '15px';
	const backdrop = document.createElement('div');
	backdrop.className = 'modal-backdrop fade show';
	document.body.append(backdrop);
}

describe('releaseModal', () => {
	afterEach(() => {
		document.body.innerHTML = '';
		document.body.className = '';
		document.body.removeAttribute('style');
	});

	it('releases <body> when a shown modal is destroyed', () => {
		const el = modalElement(true);
		lockBody();
		releaseModal(el);
		expect(document.body.classList.contains('modal-open')).toBe(false);
		expect(document.body.style.overflow).toBe('');
		expect(document.body.style.paddingRight).toBe('');
		expect(document.querySelector('.modal-backdrop')).toBeNull();
	});

	it('releases <body> while the modal is still fading out (no .show, backdrop left)', () => {
		const el = modalElement(false);
		lockBody();
		releaseModal(el);
		expect(document.body.classList.contains('modal-open')).toBe(false);
		expect(document.querySelector('.modal-backdrop')).toBeNull();
	});

	it('leaves <body> alone while another modal is shown', () => {
		const el = modalElement(true);
		modalElement(true);
		lockBody();
		releaseModal(el);
		expect(document.body.classList.contains('modal-open')).toBe(true);
		expect(document.body.style.overflow).toBe('hidden');
		expect(document.body.style.paddingRight).toBe('15px');
		expect(document.querySelector('.modal-backdrop')).not.toBeNull();
	});

	it('changes nothing for a closed modal and a clean <body>', () => {
		const el = modalElement(false);
		document.body.style.color = 'red';
		releaseModal(el);
		expect(document.body.getAttribute('style')).toBe('color: red;');
		expect(document.body.className).toBe('');
	});
});
