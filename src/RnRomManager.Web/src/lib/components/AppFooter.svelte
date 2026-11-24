<script lang="ts">
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { api } from '../api/client';
	import { jobLabel, percent } from '../jobs';
	import { hotkeys } from '../stores/hotkeys.svelte';
	import { jobs } from '../stores/jobs.svelte';
	import type { About, Me } from '../api/types';
	import AboutModal from './AboutModal.svelte';
	import Icon from './Icon.svelte';

	/**
	 * B10–B12: the signed-in footer, fixed to the bottom: About (U2: the app logo), the version (a link to the releases) and the keyboard
	 * shortcuts button (U1: a keyboard icon; hidden while the password must be changed, like the rest of the app). ROM Zip Config B17: for
	 * Admins, the running job's progress, linking to the Jobs page (not while the password must be changed: the API
	 * answers 403 then).
	 */
	interface Props {
		me: Me;
		/** Opens the keyboard shortcuts (B25). */
		onhotkeys: () => void;
	}

	let { me, onhotkeys }: Props = $props();

	const releasesUrl = 'https://github.com/rniemand/RnRomManager/releases';
	let about = $state<About | null>(null);
	let error = $state<unknown>(null);
	let aboutOpen = $state(false);

	// B11: once after sign-in; /api/about also answers while the password must be changed.
	async function load(): Promise<void> {
		error = null;
		try {
			about = await api.get<About>('/api/about');
		} catch (e) {
			error = e;
		}
	}

	onMount(() => void load());

	const showJobs = $derived(me.roles.includes('admin') && !me.mustChangePassword);
	$effect(() => {
		if (showJobs) return jobs.watch();
	});
	const job = $derived(showJobs ? jobs.current : null);
	/** Release editing B29: the Admin quick actions, run as their hotkeys. */
	const showAdmin = $derived(showJobs);
</script>

<footer class="app-footer fixed-bottom bg-body-tertiary border-top small">
	<!-- Release editing B28: one row that never wraps; below sm the labels are visually hidden, the icons stay. -->
	<div class="container d-flex flex-nowrap align-items-center gap-3 py-1 overflow-hidden">
		<button
			type="button"
			class="btn btn-link btn-sm p-0 footer-action link-body-emphasis text-decoration-none"
			aria-label="About"
			title="About"
			onclick={() => (aboutOpen = true)}
			><!-- U2: the app logo, the same file as the browser tab icon. --><img
				src="/favicon.svg"
				alt=""
				width="16"
				height="16"
				class="footer-logo"
			/></button
		>
		<a
			class="link-body-emphasis text-decoration-none"
			href={releasesUrl}
			target="_blank"
			rel="noopener noreferrer"
			><!-- U9: the name hides below sm; until /api/about loads it stays, so the link is never empty. -->{#if about}<span
					class="d-none d-sm-inline">RnRomManager&#32;</span
				>{about.version}{:else}RnRomManager{/if}</a
		>
		{#if job}
			<a
				class="footer-job d-flex align-items-center gap-2 link-body-emphasis text-decoration-none ms-auto"
				href={resolve('/jobs')}
				title="Jobs"
			>
				<span class="footer-label">{jobLabel(job.type)} {job.current}/{job.total}</span>
				<span
					class="progress"
					role="progressbar"
					aria-label="{jobLabel(job.type)} progress"
					aria-valuenow={job.total > 0 ? percent(job.current, job.total) : undefined}
					aria-valuemin="0"
					aria-valuemax="100"
				>
					{#if job.total > 0}
						<span class="progress-bar" style="width: {percent(job.current, job.total)}%"></span>
					{:else}
						<span class="progress-bar progress-bar-striped progress-bar-animated w-100"></span>
					{/if}
				</span>
			</a>
		{/if}
		{#if showAdmin}
			<button
				type="button"
				class="btn btn-link btn-sm p-0 footer-action {job ? '' : 'ms-auto'} link-body-emphasis"
				aria-label="Next missing image"
				title="Next missing image"
				onclick={() => void hotkeys.run('reports.missingImage.next')}
				><Icon name="report-missing-images" /></button
			>
			<button
				type="button"
				class="btn btn-link btn-sm p-0 footer-action link-body-emphasis"
				aria-label="Next missing IGDB ID"
				title="Next missing IGDB ID"
				onclick={() => void hotkeys.run('reports.missingIgdbId.next')}
				><Icon name="report-missing-igdb" /></button
			>
			<button
				type="button"
				class="btn btn-link btn-sm p-0 footer-action link-body-emphasis"
				aria-label="Open the first review item"
				title="Open the first review item"
				onclick={() => void hotkeys.run('review.next')}><Icon name="review" /></button
			>
		{/if}
		{#if !me.mustChangePassword}
			<button
				type="button"
				class="btn btn-link btn-sm p-0 footer-action {job || showAdmin
					? ''
					: 'ms-auto'} link-body-emphasis"
				aria-label="Shortcuts"
				title="Keyboard Shortcuts"
				onclick={onhotkeys}><Icon name="keyboard" /></button
			>
		{/if}
	</div>
</footer>

<AboutModal bind:open={aboutOpen} {about} {error} onretry={load} />

<style>
	/* U2: Bootstrap Icons' baseline, so the logo doesn't change the row's height. */
	.footer-logo {
		vertical-align: -0.125em;
	}

	/* Below sm the icon buttons are bigger, easier tap targets (the footer height grows to match in app.css). */
	@media (max-width: 575.98px) {
		.footer-action {
			display: inline-flex;
			align-items: center;
			justify-content: center;
			min-width: 2.5rem;
			min-height: 2.5rem;
			font-size: 1.25rem;
		}
		.footer-logo {
			width: 1.25rem;
			height: 1.25rem;
			vertical-align: 0;
		}
	}

	.footer-job .progress {
		width: 6rem;
		height: 0.5rem;
	}
</style>
