<script lang="ts">
	import { onMount } from 'svelte';
	import { SvelteSet } from 'svelte/reactivity';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { DatVersion, Job, Page, PendingSystem } from '#lib/api/types.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FileSize from '#lib/components/FileSize.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { formatSummary, percent, statusClass } from '#lib/jobs.js';
	import { pageCount } from '#lib/pageSize.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	let pending = $state<PendingSystem[] | null>(null);
	/** G1/G2: fixed 20 rows per page, no selector. */
	const pageSize = 20;

	let runs = $state<Page<Job> | null>(null);
	let runsPage = $state(1);
	/** Client-side page per system's pending table. */
	let filePages = $state<Record<string, number>>({});
	let datsMissing = $state(false);
	let error = $state<unknown>(null);
	let busy = $state(false);

	const job = $derived(jobs.current);
	const withFiles = $derived(pending?.filter((s) => s.files.length) ?? []);

	// IN2: the files to import, keyed `system/name`; everything is selected again after each reload.
	const key = (system: string, name: string) => `${system}/${name}`;
	const selected = new SvelteSet<string>();
	const allKeys = $derived(withFiles.flatMap((s) => s.files.map((f) => key(s.system, f.name))));
	const selectedCount = $derived(allKeys.filter((k) => selected.has(k)).length);

	function selectAll(next: boolean): void {
		selected.clear();
		if (next) allKeys.forEach((k) => selected.add(k));
	}

	function toggle(k: string): void {
		if (!selected.delete(k)) selected.add(k);
	}

	function systemState(system: PendingSystem): 'all' | 'none' | 'some' {
		const count = system.files.filter((f) => selected.has(key(system.system, f.name))).length;
		return count === 0 ? 'none' : count === system.files.length ? 'all' : 'some';
	}

	function toggleSystem(system: PendingSystem): void {
		const next = systemState(system) !== 'all';
		for (const f of system.files) {
			if (next) selected.add(key(system.system, f.name));
			else selected.delete(key(system.system, f.name));
		}
	}

	async function load(): Promise<void> {
		error = null;
		try {
			const [p, r, dats] = await Promise.all([
				api.get<PendingSystem[]>('/api/ingest/pending'),
				fetchRuns(),
				api.get<DatVersion[]>('/api/admin/dats')
			]);
			pending = p;
			selected.clear();
			for (const s of p) for (const f of s.files) selected.add(key(s.system, f.name));
			runs = r;
			datsMissing = dats.length === 0;
		} catch (e) {
			error = e;
		}
	}

	const fetchRuns = () =>
		api.get<Page<Job>>(`/api/jobs?type=Ingest&page=${runsPage}&pageSize=${pageSize}`);

	async function showRuns(page: number): Promise<void> {
		runsPage = page;
		try {
			runs = await fetchRuns();
		} catch (e) {
			error = e;
		}
	}

	function filePage(system: string, total: number): number {
		return Math.min(filePages[system] ?? 1, pageCount(total, pageSize));
	}

	onMount(() => {
		if (!auth.isAdmin) return;
		void load();
		const stopWatching = jobs.watch();
		// A finished run changes both the pending files and the history.
		const stopEnded = jobs.onEnded(() => void load());
		return () => {
			stopWatching();
			stopEnded();
		};
	});

	/** Without `files`, every pending file (IN1). */
	async function start(files?: { system: string; name: string }[]): Promise<void> {
		busy = true;
		try {
			await api.post('/api/jobs/ingest', files ? { files } : undefined);
			await jobs.refresh();
		} catch (e) {
			if (isApiError(e, 'DatsMissing')) datsMissing = true;
			else if (!toasts.jobRunning(e))
				toasts.show(isApiError(e) ? e.title : 'Start failed.', 'danger');
		} finally {
			busy = false;
		}
	}

	function importSelected(): void {
		const files = withFiles.flatMap((s) =>
			s.files
				.filter((f) => selected.has(key(s.system, f.name)))
				.map((f) => ({ system: s.system, name: f.name }))
		);
		void start(files);
	}

	async function cancel(id: string): Promise<void> {
		try {
			await api.post(`/api/jobs/${id}/cancel`);
			toasts.show('Cancelling… the current item finishes first.', 'info');
		} catch (e) {
			// 404: it finished in the meantime.
			if (!(isApiError(e) && e.status === 404))
				toasts.show(isApiError(e) ? e.title : 'Cancel failed.', 'danger');
		}
		await jobs.refresh();
	}
</script>

<svelte:head><title>Ingestion · RnRomManager</title></svelte:head>

<AdminOnly>
	<h1 class="h3 mb-3"><Icon name="ingest" /> Ingestion</h1>

	{#if error}
		<ErrorAlert {error} onretry={load} />
	{:else if pending === null}
		<Loading />
	{:else}
		<section class="mb-4" aria-labelledby="run-heading">
			<h2 class="h5" id="run-heading">Run</h2>
			<div aria-live="polite">
				{#if job}
					<div class="card card-body">
						<div class="d-flex flex-wrap align-items-center gap-2 mb-2">
							<strong>{job.type}{job.mode ? ` (${job.mode})` : ''} running</strong>
							<span class="text-body-secondary small">since {formatDateTime(job.startedAt)}</span>
							<button
								type="button"
								class="btn btn-sm btn-outline-danger ms-auto"
								onclick={() => cancel(job.id)}>Cancel</button
							>
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
						<div class="small text-body-secondary text-break">
							{job.current} / {job.total}{job.currentItem ? ` · ${job.currentItem}` : ''}
						</div>
					</div>
				{:else if datsMissing}
					<EmptyState
						icon="warning"
						message="DATs aren't loaded yet, so files can't be identified."
					>
						{#snippet action()}
							<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- route arrives in S47 -->
							<a class="btn btn-primary" href="/admin/dats">Go to Admin → DATs</a>
						{/snippet}
					</EmptyState>
				{:else}
					<div class="d-flex flex-wrap gap-2">
						<button type="button" class="btn btn-primary" disabled={busy} onclick={() => start()}>
							{#if busy}<span class="spinner-border spinner-border-sm" aria-hidden="true"
								></span>{/if}
							<Icon name="ingest" /> Import all
						</button>
						<!-- Disabled at none (nothing to do) and at all (Import all does the same). -->
						<button
							type="button"
							class="btn btn-outline-primary"
							disabled={busy || selectedCount === 0 || selectedCount === allKeys.length}
							onclick={importSelected}
						>
							Import selected ({selectedCount})
						</button>
					</div>
				{/if}
			</div>
		</section>

		<section class="mb-4" aria-labelledby="pending-heading">
			<h2 class="h5" id="pending-heading">Pending files</h2>
			{#if withFiles.length === 0}
				<div class="card"><EmptyState message="No files waiting in the ingest folders." /></div>
			{:else}
				<div class="d-flex gap-2 mb-2">
					<button
						type="button"
						class="btn btn-sm btn-outline-secondary"
						onclick={() => selectAll(true)}>Select all</button
					>
					<button
						type="button"
						class="btn btn-sm btn-outline-secondary"
						onclick={() => selectAll(false)}>Clear selection</button
					>
				</div>
				{#each withFiles as system (system.system)}
					{@const current = filePage(system.system, system.files.length)}
					{@const state = systemState(system)}
					<h3 class="h6 mt-3"><Icon name="system-{system.system}" /> {system.system}</h3>
					<div class="card">
						<div class="table-responsive">
							<table class="table table-sm table-striped table-hover align-middle mb-0">
								<thead class="table-dark">
									<tr class="small text-uppercase text-body-secondary">
										<th scope="col">
											<input
												class="form-check-input"
												type="checkbox"
												aria-label="Select all {system.system} files"
												checked={state === 'all'}
												indeterminate={state === 'some'}
												onchange={() => toggleSystem(system)}
											/>
										</th>
										<th scope="col">Name</th>
										<th scope="col" class="text-end">Size</th>
										<th scope="col">Modified</th>
									</tr>
								</thead>
								<tbody>
									{#each system.files.slice((current - 1) * pageSize, current * pageSize) as file (file.name)}
										<tr>
											<td>
												<input
													class="form-check-input"
													type="checkbox"
													aria-label="Select {file.name}"
													checked={selected.has(key(system.system, file.name))}
													onchange={() => toggle(key(system.system, file.name))}
												/>
											</td>
											<td class="text-truncate" style="max-width: 32rem" title={file.name}
												>{file.name}</td
											>
											<td class="text-end"><FileSize bytes={file.sizeBytes} /></td>
											<td class="text-nowrap">{formatDateTime(file.modifiedAt)}</td>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
					</div>
					<Pagination
						page={current}
						pages={pageCount(system.files.length, pageSize)}
						onpage={(p) => (filePages[system.system] = p)}
						label="Pages of {system.system} files"
					/>
				{/each}
			{/if}
		</section>

		<section aria-labelledby="runs-heading">
			<h2 class="h5" id="runs-heading">Last runs</h2>
			{#if !runs || runs.items.length === 0}
				<div class="card"><EmptyState message="No ingestion has run yet." /></div>
			{:else}
				<div class="card">
					<div class="table-responsive">
						<table class="table table-sm table-striped table-hover align-middle mb-0">
							<thead class="table-dark">
								<tr class="small text-uppercase text-body-secondary">
									<th scope="col">Started</th>
									<th scope="col">Status</th>
									<th scope="col">Trigger</th>
									<th scope="col">Summary</th>
								</tr>
							</thead>
							<tbody>
								{#each runs.items as run (run.id)}
									<tr>
										<td class="text-nowrap">
											<a href={resolve('/jobs/[id]', { id: run.id })}
												>{formatDateTime(run.startedAt)}</a
											>
										</td>
										<td><span class="badge {statusClass(run.status)}">{run.status}</span></td>
										<td>{run.triggerSource}</td>
										<td class="small">{formatSummary(run.summary)}</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
				</div>
				<Pagination
					page={runsPage}
					pages={pageCount(runs.total, pageSize)}
					onpage={showRuns}
					label="Pages of runs"
				/>
			{/if}
		</section>
	{/if}
</AdminOnly>
