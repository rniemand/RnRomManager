<script lang="ts">
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { Job, JobItem, Page } from '#lib/api/types.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { outcomeClass, outcomes, statusClass } from '#lib/jobs.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';

	const pageSize = 50;

	let job = $state<Job | null>(null);
	let notFound = $state(false);
	let error = $state<unknown>(null);
	let items = $state<Page<JobItem> | null>(null);
	let itemsError = $state<unknown>(null);
	let outcome = $state('');
	let itemPage = $state(1);
	let reload = $state(0);

	const id = $derived(page.params.id ?? '');
	const base = $derived(`/api/jobs/${encodeURIComponent(id)}`);
	const pages = $derived(items ? Math.max(1, Math.ceil(items.total / pageSize)) : 1);

	$effect(() => {
		if (!auth.isAdmin) return;
		const url = base;
		void reload;
		const controller = new AbortController();
		error = null;
		notFound = false;
		api.get<Job>(url, { signal: controller.signal }).then(
			(j) => (job = j),
			(e) => {
				if (controller.signal.aborted) return;
				if (isApiError(e) && e.status === 404) notFound = true;
				else error = e;
			}
		);
		return () => controller.abort();
	});

	// Ingest jobs have items; reload them when the filter, the page or the job changes.
	$effect(() => {
		if (job?.type !== 'Ingest') return;
		void job.status;
		const params = new URLSearchParams({
			page: String(itemPage),
			pageSize: String(pageSize),
			...(outcome ? { outcome } : {})
		});
		const controller = new AbortController();
		itemsError = null;
		api.get<Page<JobItem>>(`${base}/items?${params}`, { signal: controller.signal }).then(
			(p) => (items = p),
			(e) => {
				if (!controller.signal.aborted) itemsError = e;
			}
		);
		return () => controller.abort();
	});

	// While this job runs, follow it and reload once it ends.
	$effect(() => {
		if (job?.status !== 'Running') return;
		const jobId = job.id;
		const stopWatching = jobs.watch();
		const stopEnded = jobs.onEnded((ended) => {
			if (ended.id === jobId) reload++;
		});
		return () => {
			stopWatching();
			stopEnded();
		};
	});

	const live = $derived(job && jobs.current?.id === job.id ? jobs.current : null);
	const source = (item: JobItem) =>
		item.archiveEntry ? `${item.sourcePath} › ${item.archiveEntry}` : item.sourcePath;
</script>

<svelte:head><title>Job · RnRomManager</title></svelte:head>

<AdminOnly>
	{#if job?.type === 'Integrity' || job?.type === 'MediaRetry'}
		<p><a href={resolve('/integrity')}>← Integrity</a></p>
	{:else}
		<p><a href={resolve('/ingestion')}>← Ingestion</a></p>
	{/if}

	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if notFound}
		<EmptyState icon="not-found" message="This job doesn't exist." />
	{:else if !job}
		<Loading />
	{:else}
		<h1 class="h3 mb-3">
			{job.type}{job.mode ? ` (${job.mode})` : ''} job
			<span class="badge {statusClass(job.status)} align-middle fs-6">{job.status}</span>
		</h1>

		<dl class="row">
			<dt class="col-sm-3">Started</dt>
			<dd class="col-sm-9">{formatDateTime(job.startedAt)}</dd>
			<dt class="col-sm-3">Finished</dt>
			<dd class="col-sm-9">{job.finishedAt ? formatDateTime(job.finishedAt) : '—'}</dd>
			<dt class="col-sm-3">Trigger</dt>
			<dd class="col-sm-9">{job.triggerSource}</dd>
			{#if live}
				<dt class="col-sm-3">Progress</dt>
				<dd class="col-sm-9" aria-live="polite">
					{live.current} / {live.total}{live.currentItem ? ` · ${live.currentItem}` : ''}
				</dd>
			{/if}
		</dl>

		{#if job.error}
			<div class="alert alert-danger" role="alert">{job.error}</div>
		{/if}

		{#if job.summary}
			<h2 class="h5">Summary</h2>
			<ul class="list-inline mb-4">
				{#each Object.entries(job.summary) as [key, count] (key)}
					<li class="list-inline-item">
						<span class="badge {outcomeClass(key)}">{key} {count}</span>
					</li>
				{/each}
			</ul>
		{/if}

		{#if job.type === 'Ingest'}
			<h2 class="h5">Items</h2>
			<div class="row g-2 mb-2">
				<div class="col-12 col-sm-6 col-md-3">
					<label class="form-label" for="outcome">Outcome</label>
					<select
						id="outcome"
						class="form-select"
						bind:value={outcome}
						onchange={() => (itemPage = 1)}
					>
						<option value="">All outcomes</option>
						{#each outcomes as o (o)}<option value={o}>{o}</option>{/each}
					</select>
				</div>
			</div>

			{#if itemsError}
				<ErrorAlert error={itemsError} onretry={() => reload++} />
			{:else if !items}
				<Loading />
			{:else if items.items.length === 0}
				<p class="text-body-secondary">No items.</p>
			{:else}
				<div class="table-responsive">
					<table class="table table-sm table-striped table-hover">
						<thead class="table-dark">
							<tr>
								<th scope="col">File</th>
								<th scope="col">System</th>
								<th scope="col">Outcome</th>
								<th scope="col">Reason</th>
								<th scope="col">Details</th>
							</tr>
						</thead>
						<tbody>
							{#each items.items as item (item.id)}
								<tr>
									<td class="text-break">
										{#if item.gameId}
											<a href={resolve('/games/[id]', { id: item.gameId })}>{source(item)}</a>
										{:else}
											{source(item)}
										{/if}
									</td>
									<td>{item.systemId ?? '—'}</td>
									<td><span class="badge {outcomeClass(item.outcome)}">{item.outcome}</span></td>
									<td>{item.reason ?? ''}</td>
									<td class="small text-break">{item.details ?? ''}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
				{#if pages > 1}
					<nav
						class="d-flex justify-content-center align-items-center gap-3"
						aria-label="Item pages"
					>
						<button
							type="button"
							class="btn btn-sm btn-outline-secondary"
							disabled={itemPage <= 1}
							onclick={() => itemPage--}>Previous</button
						>
						<span>Page {itemPage} of {pages}</span>
						<button
							type="button"
							class="btn btn-sm btn-outline-secondary"
							disabled={itemPage >= pages}
							onclick={() => itemPage++}>Next</button
						>
					</nav>
				{/if}
			{/if}
		{/if}
	{/if}
</AdminOnly>
