<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import DataTable from '#lib/components/common/DataTable.svelte';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { Job, Page } from '#lib/types/index.js';
	import AdminOnly from '#lib/components/common/AdminOnly.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import JobStepProgress from '#lib/components/common/JobStepProgress.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import PageTitle from '#lib/components/layout/PageTitle.svelte';
	import Pagination from '#lib/components/common/Pagination.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { isCancellable, jobLabel, percent, statusClass } from '#lib/jobs.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	/**
	 * ROM Zip Config B18: the running job (with Cancel) and the recent jobs, reached from the footer. ROM Zip Config and
	 * Jobs Restyle J1–J4: the running job is a table styled like the recent jobs, with a "No job running." row when idle.
	 */
	const pageSize = 25;

	let list = $state<Page<Job> | null>(null);
	let error = $state<unknown>(null);
	let listPage = $state(1);
	let reload = $state(0);

	const pages = $derived(list ? Math.max(1, Math.ceil(list.total / pageSize)) : 1);
	const running = $derived(jobs.current);
	const progressText = $derived(
		running
			? `${running.current} / ${running.total}${running.currentItem ? ` · ${running.currentItem}` : ''}`
			: ''
	);

	$effect(() => {
		if (!auth.isAdmin) return;
		const stopWatching = jobs.watch();
		const stopEnded = jobs.onEnded(() => reload++);
		return () => {
			stopWatching();
			stopEnded();
		};
	});

	$effect(() => {
		if (!auth.isAdmin) return;
		void reload;
		const params = new URLSearchParams({ page: String(listPage), pageSize: String(pageSize) });
		const controller = new AbortController();
		error = null;
		api.get<Page<Job>>(`/api/jobs?${params}`, { signal: controller.signal }).then(
			(p) => (list = p),
			(e) => {
				if (!controller.signal.aborted) error = e;
			}
		);
		return () => controller.abort();
	});

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
</script>

<svelte:head><title>Jobs · RnRomManager</title></svelte:head>

<AdminOnly>
	<PageTitle icon="activity" text="Jobs" />

	<h2 class="h5">Running</h2>
	<div class="card mb-4">
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0 job-fixed-table">
				<colgroup>
					<col style="width: 14rem" />
					<col />
					<col style="width: 11rem" />
					<col style="width: 5rem" />
					<col style="width: 6rem" />
				</colgroup>
				<thead class="table-dark">
					<tr>
						<th scope="col">Job</th>
						<th scope="col">Progress</th>
						<th scope="col">Started</th>
						<th scope="col"><span class="visually-hidden">Details</span></th>
						<th scope="col"><span class="visually-hidden">Cancel</span></th>
					</tr>
				</thead>
				<tbody>
					{#if running}
						<tr>
							<td>{jobLabel(running.type)}{running.mode ? ` (${running.mode})` : ''}</td>
							<td>
								<div
									class="progress mb-1"
									role="progressbar"
									aria-label="Job progress"
									aria-valuenow={percent(running.current, running.total)}
									aria-valuemin="0"
									aria-valuemax="100"
								>
									<div
										class="progress-bar"
										style="width: {percent(running.current, running.total)}%"
									></div>
								</div>
								<div
									class="small text-body-secondary text-truncate"
									title={progressText}
									aria-live="polite"
								>
									{progressText}
								</div>
								<JobStepProgress step={running.step} />
							</td>
							<td class="text-nowrap">{formatDateTime(running.startedAt)}</td>
							<td><a href={resolve('/jobs/[id]', { id: running.id })}>Details</a></td>
							<td class="text-end">
								{#if isCancellable(running.type)}
									<Button variant="danger" outline size="sm" onclick={() => cancel(running.id)}
										>Cancel</Button
									>
								{/if}
							</td>
						</tr>
					{:else}
						<tr>
							<td colspan="5" class="text-body-secondary">No job running.</td>
						</tr>
					{/if}
				</tbody>
			</table>
		</div>
	</div>

	<h2 class="h5">Recent jobs</h2>
	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if !list}
		<Loading />
	{:else if list.items.length === 0}
		<EmptyState icon="list" message="No jobs yet." />
	{:else}
		<DataTable items={list.items} key={(job) => job.id}>
			{#snippet head()}
				<tr>
					<th scope="col">Job</th>
					<th scope="col">Trigger</th>
					<th scope="col">Status</th>
					<th scope="col">Started</th>
					<th scope="col">Ended</th>
					<th scope="col"><span class="visually-hidden">Details</span></th>
				</tr>
			{/snippet}
			{#snippet row(job)}
				<tr>
					<td>{jobLabel(job.type)}{job.mode ? ` (${job.mode})` : ''}</td>
					<td>{job.triggerSource}</td>
					<td><span class="badge {statusClass(job.status)}">{job.status}</span></td>
					<td>{formatDateTime(job.startedAt)}</td>
					<td>{job.finishedAt ? formatDateTime(job.finishedAt) : '—'}</td>
					<td><a href={resolve('/jobs/[id]', { id: job.id })}>Details</a></td>
				</tr>
			{/snippet}
		</DataTable>
		<Pagination page={listPage} {pages} onpage={(p) => (listPage = p)} label="Job pages" />
	{/if}
</AdminOnly>
