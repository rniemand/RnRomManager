<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import { jobLabel, percent } from '#lib/jobs.js';
	import { activity } from '#lib/stores/activity.svelte.js';
	import { hotkeys } from '#lib/stores/hotkeys.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import type { About, Me } from '#lib/types/index.js';
	import AboutModal from '#lib/components/modals/AboutModal.svelte';

	/**
	 * B10–B12: the signed-in footer, fixed to the bottom: About (U2: the app logo), the version (a link to the releases) and the keyboard
	 * shortcuts button (U1: a keyboard icon; hidden while the password must be changed, like the rest of the app). ROM Zip Config B17: for
	 * Admins, the running job's progress, linking to the Jobs page (not while the password must be changed: the API
	 * answers 403 then). L4: otherwise what holds the job lock (a job or e.g. "Saving changes…"), for every user.
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
	$effect(() => {
		if (!me.mustChangePassword) return activity.watch();
	});
	const held = $derived(job || me.mustChangePassword ? null : activity.current);
	const heldLabel = $derived(
		held ? (held.jobType ? `${jobLabel(held.jobType)} running` : `${held.activity}…`) : ''
	);
	/** Release editing B29: the Admin quick actions, run as their hotkeys. */
	const showAdmin = $derived(showJobs);
</script>

<footer class="app-footer fixed-bottom bg-body-tertiary border-top small">
	<!-- Release editing B28: one row that never wraps; below sm the labels are visually hidden, the icons stay. -->
	<div class="container d-flex flex-nowrap align-items-center gap-3 py-1 overflow-hidden">
		<Button
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
			/></Button
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
		{:else if held}
			<span class="footer-activity ms-auto" role="status">
				{#if showJobs && held.jobType}
					<a
						class="d-flex align-items-center gap-2 link-body-emphasis text-decoration-none"
						href={resolve('/jobs')}
						title="Jobs"
					>
						<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>
						<span class="footer-label">{heldLabel}</span>
					</a>
				{:else}
					<span class="d-flex align-items-center gap-2">
						<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>
						<span class="footer-label">{heldLabel}</span>
					</span>
				{/if}
			</span>
		{/if}
		{#if showAdmin}
			<Button
				icon="report-missing-images"
				class="btn btn-link btn-sm p-0 footer-action {job || held
					? ''
					: 'ms-auto'} link-body-emphasis"
				aria-label="Next missing image"
				title="Next missing image"
				onclick={() => void hotkeys.run('reports.missingImage.next')}
			/>
			<Button
				icon="report-missing-igdb"
				class="btn btn-link btn-sm p-0 footer-action link-body-emphasis"
				aria-label="Next missing IGDB ID"
				title="Next missing IGDB ID"
				onclick={() => void hotkeys.run('reports.missingIgdbId.next')}
			/>
			<Button
				icon="review"
				class="btn btn-link btn-sm p-0 footer-action link-body-emphasis"
				aria-label="Open the first review item"
				title="Open the first review item"
				onclick={() => void hotkeys.run('review.next')}
			/>
		{/if}
		{#if !me.mustChangePassword}
			<Button
				icon="keyboard"
				class="btn btn-link btn-sm p-0 footer-action {job || held || showAdmin
					? ''
					: 'ms-auto'} link-body-emphasis"
				aria-label="Shortcuts"
				title="Keyboard Shortcuts"
				onclick={onhotkeys}
			/>
		{/if}
	</div>
</footer>

<AboutModal bind:open={aboutOpen} {about} {error} onretry={load} />

<style>
	/* U2: Bootstrap Icons' baseline, so the logo doesn't change the row's height. */
	.footer-logo {
		vertical-align: -0.125em;
	}

	/* Below sm the icon buttons are bigger, easier tap targets (the footer height grows to match, below). */
	@media (max-width: 575.98px) {
		:global(.footer-action) {
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

	/* B10: the fixed footer; the last content and the toasts stay above it. */
	:global(:root) {
		--rnrm-footer-height: 2.25rem;
	}
	/* Below sm the footer's icon buttons are 2.5rem tap targets, plus the row's padding. */
	@media (max-width: 575.98px) {
		:global(:root) {
			--rnrm-footer-height: 3rem;
		}
	}
	.app-footer {
		min-height: var(--rnrm-footer-height);
		/* Release editing B28: clear of the phone's home indicator (viewport-fit=cover). */
		padding-bottom: env(safe-area-inset-bottom);
	}
	:global(main.above-footer) {
		padding-bottom: calc(
			var(--rnrm-footer-height) + env(safe-area-inset-bottom) + 1.5rem
		) !important;
	}
	:global(body:has(.app-footer) .toast-container) {
		bottom: calc(var(--rnrm-footer-height) + env(safe-area-inset-bottom)) !important;
	}
	/* B28: below sm the footer's labels stay the accessible names but take no room (Bootstrap's .visually-hidden). */
	@media (max-width: 575.98px) {
		.footer-label {
			width: 1px !important;
			height: 1px !important;
			padding: 0 !important;
			margin: -1px !important;
			overflow: hidden !important;
			clip: rect(0, 0, 0, 0) !important;
			white-space: nowrap !important;
			border: 0 !important;
			position: absolute !important;
		}
	}
</style>
