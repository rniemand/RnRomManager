<script lang="ts">
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { Job, Page } from '#lib/api/types.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import JobStepProgress from '#lib/components/JobStepProgress.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import PageTitle from '#lib/components/PageTitle.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { isCancellable, jobLabel, percent, statusClass } from '#lib/jobs.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	/** ROM Zip Config B18: the running job (with Cancel) and the recent jobs, reached from the footer. */
	const pageSize = 25;

	let list = $state<Page<Job> | null>(null);
	let error = $state<unknown>(null);
	let listPage = $state(1);
	let reload = $state(0);

	const pages = $derived(list ? Math.max(1, Math.ceil(list.total / pageSize)) : 1);
	const running = $derived(jobs.current);

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
	{#if running}
		<div class="card card-body mb-4">
			<div class="d-flex flex-wrap align-items-center gap-2 mb-2">
				<strong>{jobLabel(running.type)}{running.mode ? ` (${running.mode})` : ''}</strong>
				<a href={resolve('/jobs/[id]', { id: running.id })}>Details</a>
				{#if isCancellable(running.type)}
					<button
						type="button"
						class="btn btn-sm btn-outline-danger ms-auto"
						onclick={() => cancel(running.id)}>Cancel</button
					>
				{/if}
			</div>
			<div
				class="progress mb-1"
				role="progressbar"
				aria-label="Job progress"
				aria-valuenow={percent(running.current, running.total)}
				aria-valuemin="0"
				aria-valuemax="100"
			>
				<div class="progress-bar" style="width: {percent(running.current, running.total)}%"></div>
			</div>
			<div class="small text-body-secondary text-break" aria-live="polite">
				{running.current} / {running.total}{running.currentItem ? ` · ${running.currentItem}` : ''}
			</div>
			<JobStepProgress step={running.step} />
		</div>
	{:else}
		<p class="text-body-secondary mb-4">No job running.</p>
	{/if}

	<h2 class="h5">Recent jobs</h2>
	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if !list}
		<Loading />
	{:else if list.items.length === 0}
		<EmptyState icon="list" message="No jobs yet." />
	{:else}
		<div class="table-responsive">
			<table class="table table-sm align-middle">
				<thead>
					<tr>
						<th scope="col">Job</th>
						<th scope="col">Trigger</th>
						<th scope="col">Status</th>
						<th scope="col">Started</th>
						<th scope="col">Ended</th>
						<th scope="col"><span class="visually-hidden">Details</span></th>
					</tr>
				</thead>
				<tbody>
					{#each list.items as job (job.id)}
						<tr>
							<td>{jobLabel(job.type)}{job.mode ? ` (${job.mode})` : ''}</td>
							<td>{job.triggerSource}</td>
							<td><span class="badge {statusClass(job.status)}">{job.status}</span></td>
							<td>{formatDateTime(job.startedAt)}</td>
							<td>{job.finishedAt ? formatDateTime(job.finishedAt) : '—'}</td>
							<td><a href={resolve('/jobs/[id]', { id: job.id })}>Details</a></td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
		<Pagination page={listPage} {pages} onpage={(p) => (listPage = p)} label="Job pages" />
	{/if}
</AdminOnly>
