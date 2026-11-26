<script lang="ts">
	import Select from '#lib/components/forms/Select.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import DataTable from '#lib/components/common/DataTable.svelte';
	import PageTitle from '#lib/components/layout/PageTitle.svelte';
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { IntegrityIssue, IntegrityStatus, Job, Page } from '#lib/types/index.js';
	import AccordionItem from '#lib/components/layout/AccordionItem.svelte';
	import AdminOnly from '#lib/components/common/AdminOnly.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import RomToolsTabs from '#lib/components/layout/RomToolsTabs.svelte';
	import JobStepProgress from '#lib/components/common/JobStepProgress.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import { formatDateTime } from '#lib/format.js';
	import {
		formatSummary,
		isCancellable,
		issueStatuses,
		issueTypes,
		jobLabel,
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
	/** S4, ROM Zip Config B19: games still waiting for the storage job (either direction); the button shows while any remain. */
	let pendingGames = $state(0);

	let issues = $state<Page<IntegrityIssue> | null>(null);
	let issuesError = $state<unknown>(null);
	let issueStatus = $state('Open');
	let issueType = $state('');
	let issuePage = $state(1);
	let reloadIssues = $state(0);
	/** B24: independent panels, Issues open by default. */
	/** One accordion panel open at a time; Issues starts open. */
	let openPanel = $state<'issues' | 'history' | null>('issues');

	const job = $derived(jobs.current);
	const pages = $derived(issues ? Math.max(1, Math.ceil(issues.total / pageSize)) : 1);

	async function load(): Promise<void> {
		error = null;
		try {
			const [s, integrity, media, migration] = await Promise.all([
				api.get<IntegrityStatus>('/api/integrity/status'),
				api.get<Page<Job>>('/api/jobs?type=Integrity&pageSize=10'),
				api.get<Page<Job>>('/api/jobs?type=MediaRetry&pageSize=10'),
				api.get<{ pendingGames: number } | undefined>('/api/jobs/storage-migration')
			]);
			status = s;
			pendingGames = migration?.pendingGames ?? 0;
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
	<PageTitle icon="integrity" text="Integrity" />
	<RomToolsTabs />

	{#if error}
		<ErrorAlert {error} onretry={load} />
	{:else if !status}
		<Loading />
	{:else}
		<section class="mb-4" aria-labelledby="status-heading">
			<h2 class="visually-hidden" id="status-heading">Status</h2>
			<!-- B23: a key/value table in the house style (T1–T4). -->
			<div class="card mb-3">
				<div class="table-responsive">
					<table class="table table-sm table-striped table-hover align-middle mb-0">
						<tbody>
							{#each [['Last quick check', status.lastQuick], ['Last deep check', status.lastDeep]] as const as [label, run] (label)}
								<tr>
									<th scope="row">{label}</th>
									<td>
										{#if run}
											<a class="text-nowrap" href={resolve('/jobs/[id]', { id: run.id })}
												>{formatDateTime(run.startedAt)}</a
											>
											<span class="badge {statusClass(run.status)}">{run.status}</span>
										{:else}
											Never
										{/if}
									</td>
								</tr>
							{/each}
							<tr>
								<th scope="row">Next scheduled check</th>
								<td class="text-nowrap">
									{status.nextScheduledAt
										? formatDateTime(status.nextScheduledAt)
										: 'Not scheduled'}
								</td>
							</tr>
						</tbody>
					</table>
				</div>
			</div>

			<div>
				{#if job}
					<div class="card card-body">
						<div class="d-flex flex-wrap align-items-center gap-2 mb-2">
							<strong>{jobLabel(job.type)}{job.mode ? ` (${job.mode})` : ''} running</strong>
							{#if isCancellable(job.type)}
								<Button
									variant="danger"
									outline
									size="sm"
									class="ms-auto"
									onclick={() => cancel(job.id)}>Cancel</Button
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
						<Button
							variant="primary"
							disabled={busy}
							onclick={() => start('/api/jobs/integrity', { mode: 'Quick' })}>Run quick</Button
						>
						<Button
							variant="primary"
							outline
							disabled={busy}
							onclick={() => start('/api/jobs/integrity', { mode: 'Deep' })}>Run deep</Button
						>
						<Button
							variant="secondary"
							outline
							icon="retry"
							disabled={busy}
							onclick={() => start('/api/jobs/media-retry')}>Retry missing media</Button
						>
						{#if pendingGames > 0}
							<Button
								variant="warning"
								outline
								disabled={busy}
								onclick={() => start('/api/jobs/storage-migration')}
								>Resume ROM storage moves ({pendingGames})</Button
							>
						{/if}
					</div>
				{/if}
			</div>
		</section>
	{/if}
	<AccordionItem
		id="issues-panel"
		headingId="issues-heading"
		bind:open={() => openPanel === 'issues', (v) => (openPanel = v ? 'issues' : null)}
	>
		{#snippet header()}Issues{/snippet}
		<div class="row g-2 mb-2">
			<div class="col-6 col-md-3">
				<label class="form-label" for="issue-status">Status</label>
				<Select id="issue-status" bind:value={issueStatus} onchange={filterChanged}
					><option value="">Any status</option>
					{#each issueStatuses as s (s)}<option value={s}>{s}</option>{/each}</Select
				>
			</div>
			<div class="col-6 col-md-3">
				<label class="form-label" for="issue-type">Type</label>
				<Select id="issue-type" bind:value={issueType} onchange={filterChanged}
					><option value="">Any type</option>
					{#each issueTypes as t (t)}<option value={t}>{t}</option>{/each}</Select
				>
			</div>
		</div>

		{#if issuesError}
			<ErrorAlert error={issuesError} onretry={() => reloadIssues++} />
		{:else if !issues}
			<Loading />
		{:else if issues.items.length === 0}
			<EmptyState message="No issues." />
		{:else}
			<DataTable items={issues.items} key={(issue) => issue.id} class="mb-2">
				{#snippet head()}
					<tr>
						<th scope="col">Severity</th>
						<th scope="col">Type</th>
						<th scope="col">Path</th>
						<th scope="col">Details</th>
						<th scope="col">Status</th>
						<th scope="col">Detected</th>
					</tr>
				{/snippet}
				{#snippet row(issue)}
					<tr>
						<td><span class="badge {severityClass(issue.severity)}">{issue.severity}</span></td>
						<td>{issue.type}</td>
						<td class="text-break">
							{#if issue.gameId}
								<a href={resolve('/games/[id]', { id: issue.gameId })}>{issue.zipPath ?? 'Game'}</a>
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
				{/snippet}
			</DataTable>
			{#if pages > 1}
				<nav
					class="d-flex justify-content-center align-items-center gap-3"
					aria-label="Issue pages"
				>
					<Button
						variant="secondary"
						outline
						size="sm"
						disabled={issuePage <= 1}
						onclick={() => issuePage--}>Previous</Button
					>
					<span>Page {issuePage} of {pages}</span>
					<Button
						variant="secondary"
						outline
						size="sm"
						disabled={issuePage >= pages}
						onclick={() => issuePage++}>Next</Button
					>
				</nav>
			{/if}
		{/if}
	</AccordionItem>
	<AccordionItem
		id="history-panel"
		headingId="history-heading"
		bind:open={() => openPanel === 'history', (v) => (openPanel = v ? 'history' : null)}
	>
		{#snippet header()}History{/snippet}
		{#if history.length === 0}
			<EmptyState message="No checks have run yet." />
		{:else}
			<DataTable items={history} key={(run) => run.id}>
				{#snippet head()}
					<tr>
						<th scope="col">Started</th>
						<th scope="col">Job</th>
						<th scope="col">Status</th>
						<th scope="col">Trigger</th>
						<th scope="col">Summary</th>
					</tr>
				{/snippet}
				{#snippet row(run)}
					<tr>
						<td class="text-nowrap">
							<a href={resolve('/jobs/[id]', { id: run.id })}>{formatDateTime(run.startedAt)}</a>
						</td>
						<td>{jobLabel(run.type)}{run.mode ? ` (${run.mode})` : ''}</td>
						<td><span class="badge {statusClass(run.status)}">{run.status}</span></td>
						<td>{run.triggerSource}</td>
						<td class="small">{formatSummary(run.summary)}</td>
					</tr>
				{/snippet}
			</DataTable>
		{/if}
	</AccordionItem>
</AdminOnly>
