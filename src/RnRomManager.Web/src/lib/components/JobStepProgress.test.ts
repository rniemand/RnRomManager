import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import JobStepProgress from './JobStepProgress.svelte';

describe('JobStepProgress', () => {
	it('renders nothing without a step', () => {
		expect(render(JobStepProgress, { step: null }).container.querySelector('.job-step')).toBeNull();
		expect(
			render(JobStepProgress, { step: undefined }).container.querySelector('.job-step')
		).toBeNull();
	});

	it('renders the current file bar and its line', () => {
		render(JobStepProgress, {
			step: { phase: 'CopyingToLibrary', bytesDone: 300, bytesTotal: 1000, bytesPerSecond: null }
		});
		const bar = screen.getByRole('progressbar', { name: 'Current file progress' });
		expect(bar.getAttribute('aria-valuenow')).toBe('30');
		expect(screen.getByText('Copying to library · 300 B / 1000 B')).toBeTruthy();
	});
});
