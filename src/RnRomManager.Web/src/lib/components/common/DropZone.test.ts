import { describe, expect, it, vi } from 'vitest';
import { createRawSnippet } from 'svelte';
import { fireEvent, render, screen } from '@testing-library/svelte';
import DropZone from './DropZone.svelte';

const children = createRawSnippet(() => ({ render: () => '<p>Covers</p>' }));
const image = new File(['x'], 'a.png', { type: 'image/png' });
const text = new File(['x'], 'b.txt', { type: 'text/plain' });

function transfer(files: File[], types = ['Files']) {
	return { types, files } as unknown as DataTransfer;
}

function renderZone(props: Partial<Parameters<typeof render<typeof DropZone>>[1]> = {}) {
	const ondrop = vi.fn();
	const onreject = vi.fn();
	const result = render(DropZone, {
		hint: 'Drop images here',
		accept: (f: File) => f.type.startsWith('image/'),
		ondrop,
		onreject,
		children,
		...props
	});
	const zone = screen.getByText('Covers').parentElement!;
	return { ...result, zone, ondrop, onreject };
}

describe('DropZone', () => {
	it('shows the hint and highlights while files are dragged over (Z1)', async () => {
		const { zone } = renderZone();
		expect(screen.getByText('Drop images here')).toBeTruthy();
		expect(zone.classList.contains('border-dashed')).toBe(false);
		await fireEvent.dragOver(zone, { dataTransfer: transfer([]) });
		expect(zone.className).toContain('border border-2 border-primary border-dashed');
		expect(zone.classList.contains('bg-primary-subtle')).toBe(true);
		await fireEvent.dragLeave(zone, { relatedTarget: document.body });
		expect(zone.classList.contains('border-dashed')).toBe(false);
	});

	it('ignores a drag without files', async () => {
		const { zone } = renderZone();
		await fireEvent.dragOver(zone, { dataTransfer: transfer([], ['text/plain']) });
		expect(zone.classList.contains('border-dashed')).toBe(false);
	});

	it('passes accepted files to ondrop and rejected ones to onreject', async () => {
		const { zone, ondrop, onreject } = renderZone();
		await fireEvent.dragOver(zone, { dataTransfer: transfer([]) });
		await fireEvent.drop(zone, { dataTransfer: transfer([image, text]) });
		expect(ondrop).toHaveBeenCalledWith([image]);
		expect(onreject).toHaveBeenCalledWith([text]);
		expect(zone.classList.contains('border-dashed')).toBe(false);
	});

	it('calls only onreject when nothing is accepted, and all files go to ondrop without accept', async () => {
		const first = renderZone();
		await fireEvent.drop(first.zone, { dataTransfer: transfer([text]) });
		expect(first.ondrop).not.toHaveBeenCalled();
		expect(first.onreject).toHaveBeenCalledWith([text]);
		first.unmount();

		const second = renderZone({ accept: undefined });
		await fireEvent.drop(second.zone, { dataTransfer: transfer([image, text]) });
		expect(second.ondrop).toHaveBeenCalledWith([image, text]);
		expect(second.onreject).not.toHaveBeenCalled();
	});

	it('ignores drags and drops when disabled', async () => {
		const { zone, ondrop } = renderZone({ disabled: true });
		await fireEvent.dragOver(zone, { dataTransfer: transfer([]) });
		expect(zone.classList.contains('border-dashed')).toBe(false);
		await fireEvent.drop(zone, { dataTransfer: transfer([image]) });
		expect(ondrop).not.toHaveBeenCalled();
	});
});
