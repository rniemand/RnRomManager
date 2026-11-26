<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import DataTable from '#lib/components/common/DataTable.svelte';
	import IngestDeleteModal from '#lib/components/modals/IngestDeleteModal.svelte';
	import { replaceUrl } from '#lib/urlState.js';
	import PageTitle from '#lib/components/layout/PageTitle.svelte';
	import { onMount } from 'svelte';
	import { SvelteSet } from 'svelte/reactivity';
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
	} from '#lib/types/index.js';
	import AccordionItem from '#lib/components/layout/AccordionItem.svelte';
	import AdminOnly from '#lib/components/common/AdminOnly.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import RomToolsTabs from '#lib/components/layout/RomToolsTabs.svelte';
	import JobStepProgress from '#lib/components/common/JobStepProgress.svelte';
	import IngestFileTable from '#lib/components/ingest/IngestFileTable.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import Pagination from '#lib/components/common/Pagination.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { deleteIngestFiles, fileKey as key, isArchive } from '#lib/ingestDelete.js';
	import { formatSummary, isCancellable, jobLabel, percent, statusClass } from '#lib/jobs.js';
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

	// B23: Pending / Failed / Last runs are accordion panels, one open at a time; Pending starts open. An old
	// `?tab=failed|runs` link opens that panel instead, then leaves the URL; toggling never writes it.
	let openPanel = $state<'pending' | 'failed' | 'runs' | null>('pending');
	const pendingFileCount = $derived(withFiles.reduce((n, s) => n + s.files.length, 0));
	const failedFileCount = $derived(failedWithFiles.reduce((n, s) => n + s.files.length, 0));

	$effect(() => {
		const tab = page.url.searchParams.get('tab');
		if (tab === null) return;
		if (tab === 'failed' || tab === 'runs') openPanel = tab;
		void replaceUrl(resolve('/ingestion'));
	});

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
	<PageTitle icon="ingest" text="Ingestion" />
	<RomToolsTabs />

	{#if error}
		<ErrorAlert {error} onretry={load} />
	{:else if pending === null}
		<Loading />
	{:else}
		<!-- B23: the Run section only shows a running job or missing DATs; the Import buttons live in Pending. -->
		{#if job || datsMissing}
			<section class="mb-4" aria-labelledby="run-heading">
				<h2 class="h5" id="run-heading">Run</h2>
				<div>
					{#if job}
						<div class="card card-body">
							<div class="d-flex flex-wrap align-items-center gap-2 mb-2">
								<strong>{jobLabel(job.type)}{job.mode ? ` (${job.mode})` : ''} running</strong>
								<span class="text-body-secondary small">since {formatDateTime(job.startedAt)}</span>
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
					{:else if datsMissing}
						<EmptyState
							icon="warning"
							message="DATs aren't loaded yet, so files can't be identified."
						>
							{#snippet action()}
								<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- route arrives in S47 -->
								<Button variant="primary" href="/admin/dats">Go to Admin → DATs</Button>
							{/snippet}
						</EmptyState>
					{/if}
				</div>
			</section>
		{/if}

		<!-- Closed panels stay mounted, so selections survive. -->
		<AccordionItem
			id="pending-panel"
			headingId="pending-heading"
			bind:open={() => openPanel === 'pending', (v) => (openPanel = v ? 'pending' : null)}
		>
			{#snippet header()}Pending files ({pendingFileCount}){/snippet}
			{#if !job && !datsMissing}
				<div class="d-flex flex-wrap gap-2 mb-3">
					<Button variant="primary" disabled={busy} onclick={() => start()}
						>{#if busy}<span class="spinner-border spinner-border-sm" aria-hidden="true"
							></span>{/if}
						<Icon name="ingest" /> Import all</Button
					>
					<!-- Disabled at none (nothing to do) and at all (Import all does the same). -->
					<Button
						variant="primary"
						outline
						disabled={busy || selectedCount === 0 || selectedCount === allKeys.length}
						onclick={importSelected}>Import selected ({selectedCount})</Button
					>
				</div>
			{/if}
			{#if withFiles.length === 0}
				<div class="card"><EmptyState message="No files waiting in the ingest folders." /></div>
			{:else}
				<div class="d-flex flex-wrap gap-2 mb-2">
					<Button
						variant="secondary"
						outline
						size="sm"
						onclick={() => selectAll(selected, allKeys, true)}>Select all</Button
					>
					<Button
						variant="secondary"
						outline
						size="sm"
						onclick={() => selectAll(selected, allKeys, false)}>Clear selection</Button
					>
					<Button
						variant="danger"
						outline
						size="sm"
						disabled={deleteDisabled || selectedCount === 0}
						onclick={() => askDelete(selectedEntries(withFiles, selected, 'pending'))}
						>Delete selected ({selectedCount})</Button
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
		</AccordionItem>

		<AccordionItem
			id="failed-panel"
			headingId="failed-heading"
			bind:open={() => openPanel === 'failed', (v) => (openPanel = v ? 'failed' : null)}
		>
			{#snippet header()}Failed files ({failedFileCount}){/snippet}
			{#if failedWithFiles.length === 0}
				<div class="card"><EmptyState message="No failed imports." /></div>
			{:else}
				<div class="d-flex flex-wrap gap-2 mb-2">
					<Button
						variant="secondary"
						outline
						size="sm"
						onclick={() => selectAll(failedSelected, failedKeys, true)}>Select all</Button
					>
					<Button
						variant="secondary"
						outline
						size="sm"
						onclick={() => selectAll(failedSelected, failedKeys, false)}>Clear selection</Button
					>
					<Button
						variant="danger"
						outline
						size="sm"
						disabled={deleteDisabled || failedCount === 0}
						onclick={() => askDelete(selectedEntries(failedWithFiles, failedSelected, 'failed'))}
						>Delete selected ({failedCount})</Button
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
		</AccordionItem>

		<AccordionItem
			id="runs-panel"
			headingId="runs-heading"
			bind:open={() => openPanel === 'runs', (v) => (openPanel = v ? 'runs' : null)}
		>
			{#snippet header()}Last runs{/snippet}
			{#if !runs || runs.items.length === 0}
				<div class="card"><EmptyState message="No ingestion has run yet." /></div>
			{:else}
				<DataTable items={runs.items} key={(run) => run.id}>
					{#snippet head()}
						<tr class="small text-uppercase text-body-secondary">
							<th scope="col">Started</th>
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
							<td><span class="badge {statusClass(run.status)}">{run.status}</span></td>
							<td>{run.triggerSource}</td>
							<td class="small">{formatSummary(run.summary)}</td>
						</tr>
					{/snippet}
				</DataTable>
				<Pagination
					page={runsPage}
					pages={pageCount(runs.total, pageSize)}
					onpage={showRuns}
					label="Pages of runs"
				/>
			{/if}
		</AccordionItem>
	{/if}

	<IngestDeleteModal
		bind:open={deleteOpen}
		entries={deleteEntries}
		busy={deleting}
		onconfirm={() => runDelete(deleteEntries)}
	/>
</AdminOnly>
