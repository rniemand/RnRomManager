import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/svelte';
import FileSize from './FileSize.svelte';

describe('FileSize', () => {
	it('renders KiB with the exact byte count as the title', () => {
		const { container } = render(FileSize, { bytes: 32768 });
		const span = container.querySelector('span.file-size')!;
		expect(span.textContent).toBe('32.0 KiB');
		expect(span.getAttribute('title')).toBe(`${(32768).toLocaleString()} bytes`);
	});

	it('renders bytes and MiB', () => {
		expect(render(FileSize, { bytes: 512 }).container.textContent).toBe('512 B');
		expect(render(FileSize, { bytes: 16 * 1024 * 1024 }).container.textContent).toBe('16.0 MiB');
	});

	it('renders GiB from 1 GiB up', () => {
		expect(render(FileSize, { bytes: 8 * 1024 ** 3 }).container.textContent).toBe(
			`${(8).toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 })} GiB`
		);
	});

	it('renders nothing for null', () => {
		expect(render(FileSize, { bytes: null }).container.querySelector('span')).toBeNull();
	});
});
