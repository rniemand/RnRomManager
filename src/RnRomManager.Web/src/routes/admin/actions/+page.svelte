<script lang="ts">
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { AdminActions } from '#lib/types/index.js';
	import ConfirmModal from '#lib/components/modals/ConfirmModal.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import { formatCount, formatDateTime } from '#lib/format.js';
	import { jobLabel } from '#lib/jobs.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	/** T1–T3: start the IGDB jobs (Pull Cover Images, Refresh IGDB Ratings) with their counts. */
	let counts = $state<AdminActions | null>(null);
	let error = $state<unknown>(null);
	let starting = $state(false);
	let confirmOpen = $state(false);

	async function load(): Promise<void> {
		error = null;
		try {
			counts = await api.get<AdminActions>('/api/admin/actions');
		} catch (e) {
			error = e;
		}
	}

	onMount(() => {
		void load();
		const stopWatching = jobs.watch();
		const stopEnded = jobs.onEnded(() => void load());
		return () => {
			stopWatching();
			stopEnded();
		};
	});

	const disabled = $derived(starting || jobs.current !== null || !counts?.igdbConfigured);
	const plural = (n: number, one: string, many: string) =>
		n === 1 ? `1 ${one}` : `${formatCount(n)} ${many}`;

	async function start(type: 'PullCovers' | 'IgdbSync', url: string): Promise<void> {
		starting = true;
		try {
			const { jobId } = await api.post<{ jobId: string }>(url);
			await jobs.refresh();
			toasts.show(`${jobLabel(type)} started`, 'success', {
				href: resolve('/jobs/[id]', { id: jobId }),
				text: 'View job'
			});
		} catch (e) {
			if (!toasts.jobRunning(e))
				toasts.show(isApiError(e) ? e.title : 'Something went wrong.', 'danger');
		} finally {
			starting = false;
		}
	}

	function confirmPull(): void {
		confirmOpen = false;
		void start('PullCovers', '/api/jobs/pull-covers');
	}
</script>

<svelte:head><title>Actions · Admin · RnRomManager</title></svelte:head>

{#if error}
	<ErrorAlert {error} onretry={load} />
{:else if !counts}
	<Loading />
{:else}
	{#if !counts.igdbConfigured}
		<div class="alert alert-info" role="status">
			Set Sources:IgdbClientId and Sources:IgdbClientSecret to use IGDB actions.
		</div>
	{/if}
	<div class="row g-3">
		<div class="col-12 col-lg-6">
			<div class="card h-100">
				<div class="card-body d-flex flex-column">
					<h2 class="card-title h5">Pull Cover Images</h2>
					<p class="card-text">
						Downloads the IGDB cover of every linked game that hasn't had one pulled yet and makes
						it the game's cover, replacing the current cover.
					</p>
					<p class="card-text text-body-secondary small" data-testid="covers-count">
						{plural(counts.coversPending, 'game to process', 'games to process')}
					</p>
					<div class="mt-auto">
						<button
							type="button"
							class="btn btn-primary"
							{disabled}
							onclick={() => (confirmOpen = true)}>Pull Cover Images</button
						>
					</div>
				</div>
			</div>
		</div>
		<div class="col-12 col-lg-6">
			<div class="card h-100">
				<div class="card-body d-flex flex-column">
					<h2 class="card-title h5">Refresh IGDB Ratings</h2>
					<p class="card-text">Fetches the IGDB rating and vote count of every linked game.</p>
					<p class="card-text text-body-secondary small" data-testid="ratings-count">
						{plural(counts.igdbLinked, 'linked game', 'linked games')} · {counts.lastIgdbSyncAt
							? `last refreshed ${formatDateTime(counts.lastIgdbSyncAt)}`
							: 'never refreshed'}
					</p>
					<div class="mt-auto">
						<button
							type="button"
							class="btn btn-primary"
							{disabled}
							onclick={() => void start('IgdbSync', '/api/jobs/igdb-sync')}
							>Refresh IGDB Ratings</button
						>
					</div>
				</div>
			</div>
		</div>
	</div>
{/if}

<ConfirmModal
	bind:open={confirmOpen}
	title="Pull Cover Images"
	confirmLabel="Pull Cover Images"
	onconfirm={confirmPull}
>
	<p class="mb-0">
		{#if counts?.coversPending === 1}
			This replaces the cover of 1 game with its IGDB cover, including covers you uploaded.
		{:else}
			This replaces the cover of {formatCount(counts?.coversPending ?? 0)} games with their IGDB cover,
			including covers you uploaded.
		{/if}
	</p>
</ConfirmModal>
