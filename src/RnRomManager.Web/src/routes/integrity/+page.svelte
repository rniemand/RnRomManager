<script lang="ts">
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { IntegrityIssue, IntegrityStatus, Job, Page } from '#lib/api/types.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import JobStepProgress from '#lib/components/JobStepProgress.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import { formatDateTime } from '#lib/format.js';
	import {
		formatSummary,
		isCancellable,
		issueStatuses,
		issueTypes,
		latestJobs,
		percent,
		severityClass,
		statusClass
	} from '#lib/jobs.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	const pageSize = 50;

	let status = $state<IntegrityStatus | null>(null);
	let history = $state<Job[]>([]);
	let error = $state<unknown>(null);
	let busy = $state(false);

	let issues = $state<Page<IntegrityIssue> | null>(null);
	let issuesError = $state<unknown>(null);
	let issueStatus = $state('Open');
	let issueType = $state('');
	let issuePage = $state(1);
	let reloadIssues = $state(0);

	const job = $derived(jobs.current);
	const pages = $derived(issues ? Math.max(1, Math.ceil(issues.total / pageSize)) : 1);

	async function load(): Promise<void> {
		error = null;
		try {
			const [s, integrity, media] = await Promise.all([
				api.get<IntegrityStatus>('/api/integrity/status'),
				api.get<Page<Job>>('/api/jobs?type=Integrity&pageSize=10'),
				api.get<Page<Job>>('/api/jobs?type=MediaRetry&pageSize=10')
			]);
			status = s;
			history = latestJobs([integrity.items, media.items], 10);
		} catch (e) {
			error = e;
		}
	}

	onMount(() => {
		if (!auth.isAdmin) return;
		void load();
		const stopWatching = jobs.watch();
		const stopEnded = jobs.onEnded(() => {
			void load();
			reloadIssues++;
		});
		return () => {
			stopWatching();
			stopEnded();
		};
	});

	$effect(() => {
		if (!auth.isAdmin) return;
		void reloadIssues;
		const params = new URLSearchParams({
			page: String(issuePage),
			pageSize: String(pageSize),
			...(issueStatus ? { status: issueStatus } : {}),
			...(issueType ? { type: issueType } : {})
		});
		const controller = new AbortController();
		issuesError = null;
		api
			.get<Page<IntegrityIssue>>(`/api/integrity/issues?${params}`, { signal: controller.signal })
			.then(
				(p) => (issues = p),
				(e) => {
					if (!controller.signal.aborted) issuesError = e;
				}
			);
		return () => controller.abort();
	});

	async function start(url: string, body?: object): Promise<void> {
		busy = true;
		try {
			await api.post(url, body);
			await jobs.refresh();
		} catch (e) {
			if (!toasts.jobRunning(e)) toasts.show(isApiError(e) ? e.title : 'Start failed.', 'danger');
		} finally {
			busy = false;
		}
	}

	async function cancel(id: string): Promise<void> {
		try {
			await api.post(`/api/jobs/${id}/cancel`);
			toasts.show('Cancelling…', 'info');
		} catch (e) {
			if (!(isApiError(e) && e.status === 404))
				toasts.show(isApiError(e) ? e.title : 'Cancel failed.', 'danger');
		}
		await jobs.refresh();
	}

	const filterChanged = () => (issuePage = 1);
</script>

<svelte:head><title>Integrity · RnRomManager</title></svelte:head>

<AdminOnly>
	<h1 class="h3 mb-3"><Icon name="integrity" /> Integrity</h1>

	{#if error}
		<ErrorAlert {error} onretry={load} />
	{:else if !status}
		<Loading />
	{:else}
		<section class="mb-4" aria-labelledby="status-heading">
			<h2 class="visually-hidden" id="status-heading">Status</h2>
			<dl class="row mb-3">
				{#each [['Last quick check', status.lastQuick], ['Last deep check', status.lastDeep]] as const as [label, run] (label)}
					<dt class="col-sm-4 col-lg-3">{label}</dt>
					<dd class="col-sm-8 col-lg-9">
						{#if run}
							<a href={resolve('/jobs/[id]', { id: run.id })}>{formatDateTime(run.startedAt)}</a>
							<span class="badge {statusClass(run.status)}">{run.status}</span>
						{:else}
							Never
						{/if}
					</dd>
				{/each}
				<dt class="col-sm-4 col-lg-3">Next scheduled check</dt>
				<dd class="col-sm-8 col-lg-9">
					{status.nextScheduledAt ? formatDateTime(status.nextScheduledAt) : 'Not scheduled'}
				</dd>
			</dl>

			<div>
				{#if job}
					<div class="card card-body">
						<div class="d-flex flex-wrap align-items-center gap-2 mb-2">
							<strong>{job.type}{job.mode ? ` (${job.mode})` : ''} running</strong>
							{#if isCancellable(job.type)}
								<button
									type="button"
									class="btn btn-sm btn-outline-danger ms-auto"
									onclick={() => cancel(job.id)}>Cancel</button
								>
							{/if}
						</div>
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
						<div class="small text-body-secondary text-break" aria-live="polite">
							{job.current} / {job.total}{job.currentItem ? ` · ${job.currentItem}` : ''}
						</div>
						<JobStepProgress step={job.step} />
					</div>
				{:else}
					<div class="d-flex flex-wrap gap-2">
						<button
							type="button"
							class="btn btn-primary"
							disabled={busy}
							onclick={() => start('/api/jobs/integrity', { mode: 'Quick' })}>Run quick</button
						>
						<button
							type="button"
							class="btn btn-outline-primary"
							disabled={busy}
							onclick={() => start('/api/jobs/integrity', { mode: 'Deep' })}>Run deep</button
						>
						<button
							type="button"
							class="btn btn-outline-secondary"
							disabled={busy}
							onclick={() => start('/api/jobs/media-retry')}
						>
							<Icon name="retry" /> Retry missing media
						</button>
					</div>
				{/if}
			</div>
		</section>
	{/if}

	<section class="mb-4" aria-labelledby="issues-heading">
		<h2 class="h5" id="issues-heading">Issues</h2>
		<div class="row g-2 mb-2">
			<div class="col-6 col-md-3">
				<label class="form-label" for="issue-status">Status</label>
				<select
					id="issue-status"
					class="form-select"
					bind:value={issueStatus}
					onchange={filterChanged}
				>
					<option value="">Any status</option>
					{#each issueStatuses as s (s)}<option value={s}>{s}</option>{/each}
				</select>
			</div>
			<div class="col-6 col-md-3">
				<label class="form-label" for="issue-type">Type</label>
				<select id="issue-type" class="form-select" bind:value={issueType} onchange={filterChanged}>
					<option value="">Any type</option>
					{#each issueTypes as t (t)}<option value={t}>{t}</option>{/each}
				</select>
			</div>
		</div>

		{#if issuesError}
			<ErrorAlert error={issuesError} onretry={() => reloadIssues++} />
		{:else if !issues}
			<Loading />
		{:else if issues.items.length === 0}
			<p class="text-body-secondary">No issues.</p>
		{:else}
			<div class="table-responsive">
				<table class="table table-sm table-striped table-hover">
					<thead class="table-dark">
						<tr>
							<th scope="col">Severity</th>
							<th scope="col">Type</th>
							<th scope="col">Path</th>
							<th scope="col">Details</th>
							<th scope="col">Status</th>
							<th scope="col">Detected</th>
						</tr>
					</thead>
					<tbody>
						{#each issues.items as issue (issue.id)}
							<tr>
								<td><span class="badge {severityClass(issue.severity)}">{issue.severity}</span></td>
								<td>{issue.type}</td>
								<td class="text-break">
									{#if issue.gameId}
										<a href={resolve('/games/[id]', { id: issue.gameId })}
											>{issue.zipPath ?? 'Game'}</a
										>
									{:else}
										{issue.zipPath ?? '—'}
									{/if}
									{#if issue.entryName}<span class="text-body-secondary">
											› {issue.entryName}</span
										>{/if}
								</td>
								<td class="small text-break">{issue.details ?? ''}</td>
								<td>{issue.status}</td>
								<td class="text-nowrap">{formatDateTime(issue.detectedAt)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			{#if pages > 1}
				<nav
					class="d-flex justify-content-center align-items-center gap-3"
					aria-label="Issue pages"
				>
					<button
						type="button"
						class="btn btn-sm btn-outline-secondary"
						disabled={issuePage <= 1}
						onclick={() => issuePage--}>Previous</button
					>
					<span>Page {issuePage} of {pages}</span>
					<button
						type="button"
						class="btn btn-sm btn-outline-secondary"
						disabled={issuePage >= pages}
						onclick={() => issuePage++}>Next</button
					>
				</nav>
			{/if}
		{/if}
	</section>

	<section aria-labelledby="history-heading">
		<h2 class="h5" id="history-heading">History</h2>
		{#if history.length === 0}
			<p class="text-body-secondary">No checks have run yet.</p>
		{:else}
			<div class="table-responsive">
				<table class="table table-sm table-striped table-hover">
					<thead class="table-dark">
						<tr>
							<th scope="col">Started</th>
							<th scope="col">Job</th>
							<th scope="col">Status</th>
							<th scope="col">Trigger</th>
							<th scope="col">Summary</th>
						</tr>
					</thead>
					<tbody>
						{#each history as run (run.id)}
							<tr>
								<td class="text-nowrap">
									<a href={resolve('/jobs/[id]', { id: run.id })}>{formatDateTime(run.startedAt)}</a
									>
								</td>
								<td>{run.type}{run.mode ? ` (${run.mode})` : ''}</td>
								<td><span class="badge {statusClass(run.status)}">{run.status}</span></td>
								<td>{run.triggerSource}</td>
								<td class="small">{formatSummary(run.summary)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
	</section>
</AdminOnly>
