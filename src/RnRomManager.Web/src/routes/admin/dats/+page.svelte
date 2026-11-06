<script lang="ts">
	import { onMount } from 'svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { DatVersion } from '#lib/api/types.js';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { percent } from '#lib/jobs.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	let dats = $state<DatVersion[] | null>(null);
	let error = $state<unknown>(null);
	let busy = $state(false);

	const job = $derived(jobs.current);

	async function load(): Promise<void> {
		error = null;
		try {
			dats = await api.get<DatVersion[]>('/api/admin/dats');
		} catch (e) {
			error = e;
		}
	}

	onMount(() => {
		if (!auth.isAdmin) return;
		void load();
		const stopWatching = jobs.watch();
		const stopEnded = jobs.onEnded((ended) => {
			if (ended.type === 'DatRefresh') void load();
		});
		return () => {
			stopWatching();
			stopEnded();
		};
	});

	async function refresh(): Promise<void> {
		busy = true;
		try {
			await api.post('/api/jobs/dat-refresh');
			await jobs.refresh();
		} catch (e) {
			if (!toasts.jobRunning(e)) toasts.show(isApiError(e) ? e.title : 'Refresh failed.', 'danger');
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>DATs · Admin · RnRomManager</title></svelte:head>

<div class="mb-3" aria-live="polite">
	{#if job}
		<div class="card card-body">
			<strong class="mb-2">{job.type} running</strong>
			<div
				class="progress mb-1"
				role="progressbar"
				aria-label="Job progress"
				aria-valuenow={percent(job.current, job.total)}
				aria-valuemin="0"
				aria-valuemax="100"
			>
				<div class="progress-bar" style="width: {percent(job.current, job.total)}%"></div>
			</div>
			<div class="small text-body-secondary text-break">
				{job.current} / {job.total}{job.currentItem ? ` · ${job.currentItem}` : ''}
			</div>
		</div>
	{:else}
		<button type="button" class="btn btn-primary" disabled={busy} onclick={refresh}>
			<Icon name="retry" /> Refresh DATs
		</button>
	{/if}
</div>

{#if error}
	<ErrorAlert {error} onretry={load} />
{:else if !dats}
	<Loading />
{:else if dats.length === 0}
	<EmptyState message="No DATs downloaded yet. Refresh to download them." />
{:else}
	<div class="card">
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0">
				<thead class="table-dark">
					<tr class="small text-uppercase text-body-secondary">
						<th scope="col">DAT</th>
						<th scope="col">Version</th>
						<th scope="col">Downloaded</th>
					</tr>
				</thead>
				<tbody>
					{#each dats as dat (dat.datName)}
						<tr>
							<td>{dat.datName}</td>
							<td>{dat.version}</td>
							<td class="text-nowrap">{formatDateTime(dat.downloadedAt)}</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
{/if}
