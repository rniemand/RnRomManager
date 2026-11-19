<script lang="ts">
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { api } from '../api/client';
	import { jobLabel, percent } from '../jobs';
	import { jobs } from '../stores/jobs.svelte';
	import type { About, Me } from '../api/types';
	import AboutModal from './AboutModal.svelte';
	import Icon from './Icon.svelte';

	/**
	 * B10–B12: the signed-in footer, fixed to the bottom: About, the version (a link to the releases) and the keyboard
	 * shortcuts button (hidden while the password must be changed, like the rest of the app). ROM Zip Config B17: for
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
</script>

<footer class="app-footer fixed-bottom bg-body-tertiary border-top small">
	<div class="container d-flex align-items-center gap-3 py-1">
		<button
			type="button"
			class="btn btn-link btn-sm p-0 link-body-emphasis text-decoration-none"
			onclick={() => (aboutOpen = true)}><Icon name="info" /> About</button
		>
		<a
			class="link-body-emphasis text-decoration-none"
			href={releasesUrl}
			target="_blank"
			rel="noopener noreferrer">RnRomManager{about ? ` ${about.version}` : ''}</a
		>
		{#if job}
			<a
				class="footer-job d-flex align-items-center gap-2 link-body-emphasis text-decoration-none ms-auto"
				href={resolve('/jobs')}
				title="Jobs"
			>
				<span>{jobLabel(job.type)} {job.current}/{job.total}</span>
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
		{#if !me.mustChangePassword}
			<button
				type="button"
				class="btn btn-link btn-sm p-0 {job ? '' : 'ms-auto'} link-body-emphasis"
				title="Keyboard shortcuts"
				onclick={onhotkeys}><Icon name="help" /> Shortcuts</button
			>
		{/if}
	</div>
</footer>

<AboutModal bind:open={aboutOpen} {about} {error} onretry={load} />

<style>
	.footer-job .progress {
		width: 6rem;
		height: 0.5rem;
	}
</style>
