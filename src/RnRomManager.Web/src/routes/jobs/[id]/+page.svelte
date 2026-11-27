<script lang="ts">
	import Select from '#lib/components/forms/Select.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import DataTable from '#lib/components/common/DataTable.svelte';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { Job, JobItem, Page } from '#lib/types/index.js';
	import AdminOnly from '#lib/components/common/AdminOnly.svelte';
	import JobStepProgress from '#lib/components/common/JobStepProgress.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import { formatDateTime } from '#lib/format.js';
	import {
		formatSummaryValue,
		outcomeClass,
		outcomes,
		jobLabel,
		percent,
		splitSummary,
		statusClass
	} from '#lib/jobs.js';
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
	const liveText = $derived(
		live ? `${live.current} / ${live.total}${live.currentItem ? ` · ${live.currentItem}` : ''}` : ''
	);
	const summary = $derived(splitSummary(job?.summary ?? null));
	const source = (item: JobItem) =>
		item.archiveEntry ? `${item.sourcePath} › ${item.archiveEntry}` : item.sourcePath;
</script>

<svelte:head><title>Job · RnRomManager</title></svelte:head>

<AdminOnly>
	<!-- JB2: button-style links, as the game page's Back to Library. -->
	<div class="d-flex flex-wrap gap-2 mb-3">
		{#if job?.type === 'Integrity' || job?.type === 'MediaRetry'}
			<Button variant="primary" outline size="sm" icon="back" href={resolve('/integrity')}
				>Back to Integrity</Button
			>
		{:else}
			<Button variant="primary" outline size="sm" icon="back" href={resolve('/ingestion')}
				>Back to Ingestion</Button
			>
		{/if}
		<Button variant="primary" outline size="sm" icon="activity" href={resolve('/jobs')}
			>All jobs</Button
		>
	</div>

	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if notFound}
		<EmptyState icon="not-found" message="This job doesn't exist." />
	{:else if !job}
		<Loading />
	{:else}
		<h1 class="h3 mb-3">
			{jobLabel(job.type)}{job.mode ? ` (${job.mode})` : ''} Job
			<span class="badge {statusClass(job.status)} align-middle fs-6">{job.status}</span>
		</h1>

		<div class="card mb-4">
			<div class="table-responsive">
				<table
					class="table table-sm table-striped table-hover align-middle mb-0"
					class:job-fixed-table={live}
				>
					{#if live}
						<colgroup>
							<col class="col-11" />
							<col class="col-11" />
							<col class="col-8" />
							<col />
						</colgroup>
					{/if}
					<thead class="table-dark">
						<tr>
							<th scope="col">Started</th>
							<th scope="col">Finished</th>
							<th scope="col">Trigger</th>
							{#if live}<th scope="col">Progress</th>{/if}
						</tr>
					</thead>
					<tbody>
						<tr>
							<td class="text-nowrap">{formatDateTime(job.startedAt)}</td>
							<td class="text-nowrap">{job.finishedAt ? formatDateTime(job.finishedAt) : '—'}</td>
							<td>{job.triggerSource}</td>
							{#if live}
								<td>
									<div
										class="progress mb-1"
										role="progressbar"
										aria-label="Job progress"
										aria-valuenow={percent(live.current, live.total)}
										aria-valuemin="0"
										aria-valuemax="100"
									>
										<div
											class="progress-bar"
											style="width: {percent(live.current, live.total)}%"
										></div>
									</div>
									<div
										class="small text-body-secondary text-truncate"
										title={liveText}
										aria-live="polite"
									>
										{liveText}
									</div>
									<JobStepProgress step={live.step} />
								</td>
							{/if}
						</tr>
					</tbody>
				</table>
			</div>
		</div>

		{#if job.error}
			<div class="alert alert-danger" role="alert">{job.error}</div>
		{/if}

		{#if job.summary}
			<h2 class="h5">Summary</h2>
			{#if summary.badges.length}
				<ul class="list-inline mb-4">
					{#each summary.badges as [key, value] (key)}
						<li class="list-inline-item">
							<span class="badge {outcomeClass(key)}">{key} {formatSummaryValue(value)}</span>
						</li>
					{/each}
				</ul>
			{/if}
			{#each summary.tables as t (t.key)}
				<h3 class="h6">{t.title}</h3>
				<DataTable items={t.rows} key={([name]) => name} class="mb-4">
					{#snippet head()}
						<tr>
							<th scope="col">{t.columns[0]}</th>
							<th scope="col">{t.columns[1]}</th>
						</tr>
					{/snippet}
					{#snippet row([name, value])}
						<tr>
							<td>{name}</td>
							<td>{value}</td>
						</tr>
					{/snippet}
				</DataTable>
			{/each}
		{/if}

		{#if job.type === 'Ingest'}
			<h2 class="h5">Items</h2>
			<div class="row g-2 mb-2">
				<div class="col-12 col-sm-6 col-md-3">
					<label class="form-label" for="outcome">Outcome</label>
					<Select id="outcome" bind:value={outcome} onchange={() => (itemPage = 1)}
						><option value="">All outcomes</option>
						{#each outcomes as o (o)}<option value={o}>{o}</option>{/each}</Select
					>
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
						<Button
							variant="secondary"
							outline
							size="sm"
							disabled={itemPage <= 1}
							onclick={() => itemPage--}>Previous</Button
						>
						<span>Page {itemPage} of {pages}</span>
						<Button
							variant="secondary"
							outline
							size="sm"
							disabled={itemPage >= pages}
							onclick={() => itemPage++}>Next</Button
						>
					</nav>
				{/if}
			{/if}
		{/if}
	{/if}
</AdminOnly>

<style>
	.col-11 {
		width: 11rem;
	}
	.col-8 {
		width: 8rem;
	}
</style>
