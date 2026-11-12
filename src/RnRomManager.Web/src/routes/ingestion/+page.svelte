<script lang="ts">
	import { onMount } from 'svelte';
	import { SvelteSet } from 'svelte/reactivity';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type {
		DatVersion,
		FailedSystem,
		IngestDeleteEntry,
		Job,
		Page,
		PendingSystem
	} from '#lib/api/types.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import ConfirmModal from '#lib/components/ConfirmModal.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import JobStepProgress from '#lib/components/JobStepProgress.svelte';
	import IngestFileTable from '#lib/components/IngestFileTable.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import { formatDateTime } from '#lib/format.js';
	import {
		deleteIngestFiles,
		deleteTitle,
		fileKey as key,
		isArchive,
		modalListSize
	} from '#lib/ingestDelete.js';
	import { formatSummary, percent, statusClass } from '#lib/jobs.js';
	import { pageCount } from '#lib/pageSize.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	let pending = $state<PendingSystem[] | null>(null);
	let failed = $state<FailedSystem[]>([]);
	/** G1/G2: fixed 20 rows per page, no selector. */
	const pageSize = 20;

	let runs = $state<Page<Job> | null>(null);
	let runsPage = $state(1);
	let datsMissing = $state(false);
	let error = $state<unknown>(null);
	let busy = $state(false);

	const job = $derived(jobs.current);
	const withFiles = $derived(pending?.filter((s) => s.files.length) ?? []);
	const failedWithFiles = $derived(failed.filter((s) => s.files.length));

	// IN2: the files to import, keyed `system/name`; everything is selected again after each reload.
	const selected = new SvelteSet<string>();
	const allKeys = $derived(withFiles.flatMap((s) => s.files.map((f) => key(s.system, f.name))));
	const selectedCount = $derived(allKeys.filter((k) => selected.has(k)).length);
	// D4: the Failed section's own selection, empty after each reload.
	const failedSelected = new SvelteSet<string>();
	const failedKeys = $derived(
		failedWithFiles.flatMap((s) => s.files.map((f) => key(s.system, f.name)))
	);
	const failedCount = $derived(failedKeys.filter((k) => failedSelected.has(k)).length);

	// I1/I2: Pending / Failed / Last runs are tabs under the Run section; the tab is URL state (`?tab=failed|runs`).
	type Tab = 'pending' | 'failed' | 'runs';
	const tab = $derived<Tab>(
		(['failed', 'runs'] as const).find((t) => t === page.url.searchParams.get('tab')) ?? 'pending'
	);
	const pendingFileCount = $derived(withFiles.reduce((n, s) => n + s.files.length, 0));
	const failedFileCount = $derived(failedWithFiles.reduce((n, s) => n + s.files.length, 0));

	function selectTab(next: Tab): void {
		const path = resolve('/ingestion');
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query
		void goto(next === 'pending' ? path : `${path}?tab=${next}`, { replace: true, reset: false });
	}

	function selectAll(set: SvelteSet<string>, keys: string[], next: boolean): void {
		set.clear();
		if (next) keys.forEach((k) => set.add(k));
	}

	// D5/D6: the entries awaiting confirmation, and whether a delete is in flight.
	let deleteEntries = $state<IngestDeleteEntry[]>([]);
	let deleteOpen = $state(false);
	let deleting = $state(false);
	const deleteDisabled = $derived(!!job || deleting);

	function selectedEntries(
		systems: { system: string; files: { name: string }[] }[],
		set: SvelteSet<string>,
		folder: IngestDeleteEntry['folder']
	): IngestDeleteEntry[] {
		return systems.flatMap((s) =>
			s.files
				.filter((f) => set.has(key(s.system, f.name)))
				.map((f) => ({ system: s.system, folder, name: f.name }))
		);
	}

	function askDelete(entries: IngestDeleteEntry[]): void {
		if (entries.length === 0) return;
		deleteEntries = entries;
		deleteOpen = true;
	}

	/** D5: Shift+click on a row skips the modal, except for an archive. */
	function rowDelete(event: MouseEvent, entry: IngestDeleteEntry): void {
		if (event.shiftKey && !isArchive(entry.name)) void runDelete([entry]);
		else askDelete([entry]);
	}

	async function runDelete(entries: IngestDeleteEntry[]): Promise<void> {
		deleting = true;
		try {
			await deleteIngestFiles(entries);
		} finally {
			deleting = false;
			deleteOpen = false;
		}
		await load();
	}

	async function load(): Promise<void> {
		error = null;
		try {
			const [p, f, r, dats] = await Promise.all([
				api.get<PendingSystem[]>('/api/ingest/pending'),
				api.get<FailedSystem[]>('/api/ingest/failed'),
				fetchRuns(),
				api.get<DatVersion[]>('/api/admin/dats')
			]);
			pending = p;
			selected.clear();
			for (const s of p) for (const file of s.files) selected.add(key(s.system, file.name));
			failed = f;
			failedSelected.clear();
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
			<div>
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
						<div class="small text-body-secondary text-break" aria-live="polite">
							{job.current} / {job.total}{job.currentItem ? ` · ${job.currentItem}` : ''}
						</div>
						<JobStepProgress step={job.step} />
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

		<ul class="nav nav-tabs mb-3" role="tablist">
			{#each [['pending', `Pending files (${pendingFileCount})`], ['failed', `Failed files (${failedFileCount})`], ['runs', 'Last runs']] as [value, label] (value)}
				<li class="nav-item" role="presentation">
					<button
						type="button"
						class="nav-link"
						class:active={tab === value}
						id="{value}-tab"
						role="tab"
						aria-selected={tab === value}
						aria-controls="{value}-panel"
						onclick={() => selectTab(value as Tab)}>{label}</button
					>
				</li>
			{/each}
		</ul>

		<!-- Panels stay mounted (hidden, not removed), so selections survive a tab switch. -->
		<div
			class="mb-4"
			id="pending-panel"
			role="tabpanel"
			aria-labelledby="pending-tab"
			hidden={tab !== 'pending'}
		>
			<h2 class="h5 visually-hidden">Pending files</h2>
			{#if withFiles.length === 0}
				<div class="card"><EmptyState message="No files waiting in the ingest folders." /></div>
			{:else}
				<div class="d-flex flex-wrap gap-2 mb-2">
					<button
						type="button"
						class="btn btn-sm btn-outline-secondary"
						onclick={() => selectAll(selected, allKeys, true)}>Select all</button
					>
					<button
						type="button"
						class="btn btn-sm btn-outline-secondary"
						onclick={() => selectAll(selected, allKeys, false)}>Clear selection</button
					>
					<button
						type="button"
						class="btn btn-sm btn-outline-danger"
						disabled={deleteDisabled || selectedCount === 0}
						onclick={() => askDelete(selectedEntries(withFiles, selected, 'pending'))}
						>Delete selected ({selectedCount})</button
					>
				</div>
				{#each withFiles as system (system.system)}
					<h3 class="h6 mt-3"><Icon name="system-{system.system}" /> {system.system}</h3>
					<IngestFileTable
						system={system.system}
						files={system.files}
						{selected}
						disabled={deleteDisabled}
						ondelete={(e, name) => rowDelete(e, { system: system.system, folder: 'pending', name })}
					/>
				{/each}
			{/if}
		</div>

		<div
			class="mb-4"
			id="failed-panel"
			role="tabpanel"
			aria-labelledby="failed-tab"
			hidden={tab !== 'failed'}
		>
			<h2 class="h5 visually-hidden">Failed files</h2>
			{#if failedWithFiles.length === 0}
				<div class="card"><EmptyState message="No failed imports." /></div>
			{:else}
				<div class="d-flex flex-wrap gap-2 mb-2">
					<button
						type="button"
						class="btn btn-sm btn-outline-secondary"
						onclick={() => selectAll(failedSelected, failedKeys, true)}>Select all</button
					>
					<button
						type="button"
						class="btn btn-sm btn-outline-secondary"
						onclick={() => selectAll(failedSelected, failedKeys, false)}>Clear selection</button
					>
					<button
						type="button"
						class="btn btn-sm btn-outline-danger"
						disabled={deleteDisabled || failedCount === 0}
						onclick={() => askDelete(selectedEntries(failedWithFiles, failedSelected, 'failed'))}
						>Delete selected ({failedCount})</button
					>
				</div>
				{#each failedWithFiles as system (system.system)}
					<h3 class="h6 mt-3"><Icon name="system-{system.system}" /> {system.system}</h3>
					<IngestFileTable
						system={system.system}
						files={system.files}
						selected={failedSelected}
						label="failed {system.system} files"
						showReason
						disabled={deleteDisabled}
						ondelete={(e, name) => rowDelete(e, { system: system.system, folder: 'failed', name })}
					/>
				{/each}
			{/if}
		</div>

		<div id="runs-panel" role="tabpanel" aria-labelledby="runs-tab" hidden={tab !== 'runs'}>
			<h2 class="h5 visually-hidden">Last runs</h2>
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
		</div>
	{/if}

	<ConfirmModal
		bind:open={deleteOpen}
		title={deleteTitle(deleteEntries)}
		confirmLabel="Delete"
		confirmClass="btn-danger"
		busy={deleting}
		onconfirm={() => runDelete(deleteEntries)}
	>
		<p>
			{deleteEntries.length === 1 ? 'This file is' : 'These files are'} permanently deleted from the ingest
			folder. This can't be undone.
			{#if deleteEntries.some((e) => e.folder === 'pending')}
				Their pending Review items are deleted too.
			{/if}
		</p>
		<ul class="mb-0 text-break">
			{#each deleteEntries.slice(0, modalListSize) as entry (`${entry.folder}/${entry.system}/${entry.name}`)}
				<li><code>{entry.system}/{entry.name}</code></li>
			{/each}
			{#if deleteEntries.length > modalListSize}
				<li>and {deleteEntries.length - modalListSize} more</li>
			{/if}
		</ul>
	</ConfirmModal>
</AdminOnly>
