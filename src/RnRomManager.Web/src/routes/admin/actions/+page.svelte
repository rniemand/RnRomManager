<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import Checkbox from '#lib/components/forms/Checkbox.svelte';
	import PullCoverImagesModal from '#lib/components/modals/PullCoverImagesModal.svelte';
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { AdminActions } from '#lib/types/index.js';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import { formatCount, formatDateTime } from '#lib/format.js';
	import { jobLabel } from '#lib/jobs.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	/**
	 * T1–T3: start the IGDB jobs (Pull Cover Images, Refresh IGDB Ratings) with their counts. AC9–AC10: by default they
	 * skip handled games; Try Missing Images and Repull ALL Ratings bring back the wider sets.
	 */
	let counts = $state<AdminActions | null>(null);
	let error = $state<unknown>(null);
	let starting = $state(false);
	let confirmOpen = $state(false);
	let retryMissing = $state(false);
	let allRatings = $state(false);

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
	const coverCount = $derived(
		counts ? (retryMissing ? counts.coversMissing : counts.coversPending) : 0
	);
	const ratingCount = $derived(
		counts ? (allRatings ? counts.igdbLinked : counts.ratingsMissing) : 0
	);

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
		void start('PullCovers', `/api/jobs/pull-covers${retryMissing ? '?retryMissing=true' : ''}`);
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
						Downloads the IGDB cover of every linked game that hasn't been tried yet and makes it
						the game's cover, replacing the current cover.
					</p>
					<p class="card-text text-body-secondary small" data-testid="covers-count">
						{plural(coverCount, 'game to process', 'games to process')}
					</p>
					<Checkbox
						id="pull-retry-missing"
						label="Try Missing Images"
						bind:checked={retryMissing}
						{disabled}
					/>
					<div class="form-text mb-3">
						Also retries games whose IGDB cover couldn't be pulled before.
					</div>
					<div class="mt-auto">
						<Button variant="primary" {disabled} onclick={() => (confirmOpen = true)}
							>Pull Cover Images</Button
						>
					</div>
				</div>
			</div>
		</div>
		<div class="col-12 col-lg-6">
			<div class="card h-100">
				<div class="card-body d-flex flex-column">
					<h2 class="card-title h5">Refresh IGDB Ratings</h2>
					<p class="card-text">
						Fetches the IGDB rating and vote count of every linked game without a rating.
					</p>
					<p class="card-text text-body-secondary small" data-testid="ratings-count">
						{plural(ratingCount, 'game to process', 'games to process')} · {counts.lastIgdbSyncAt
							? `last refreshed ${formatDateTime(counts.lastIgdbSyncAt)}`
							: 'never refreshed'}
					</p>
					<Checkbox
						id="ratings-all"
						label="Repull ALL Ratings"
						bind:checked={allRatings}
						{disabled}
					/>
					<div class="form-text mb-3">
						Refreshes every linked game, including ones that already have a rating.
					</div>
					<div class="mt-auto">
						<Button
							variant="primary"
							{disabled}
							onclick={() =>
								void start('IgdbSync', `/api/jobs/igdb-sync${allRatings ? '?all=true' : ''}`)}
							>Refresh IGDB Ratings</Button
						>
					</div>
				</div>
			</div>
		</div>
	</div>
{/if}

<PullCoverImagesModal bind:open={confirmOpen} count={coverCount} onconfirm={confirmPull} />
