<script lang="ts">
	import { fromAction } from 'svelte/attachments';
	import Button from '#lib/components/forms/Button.svelte';
	import PermanentDeleteModal from '#lib/components/modals/PermanentDeleteModal.svelte';
	import PageTitle from '#lib/components/layout/PageTitle.svelte';
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { SvelteSet } from 'svelte/reactivity';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type {
		IngestDeleteEntry,
		RemovedRestoreResponse,
		RemovedSystem,
		SystemInfo
	} from '#lib/types/index.js';
	import AdminOnly from '#lib/components/common/AdminOnly.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import RomToolsTabs from '#lib/components/layout/RomToolsTabs.svelte';
	import IngestFileTable from '#lib/components/ingest/IngestFileTable.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { formatCount } from '#lib/format.js';
	import { deleteIngestFiles, fileKey as key } from '#lib/ingestDelete.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { refreshCounts } from '#lib/stores/navCounts.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import { tooltip } from '#lib/tooltip.js';

	/** D1–D5: the files library edits moved to `ingest/<system>/_removed/`, with restore and permanent delete. */
	let removed = $state<RemovedSystem[] | null>(null);
	let names = $state<Record<string, string>>({});
	let error = $state<unknown>(null);

	// D3: one selection across every system, keyed `system/name`; empty after each reload.
	const selected = new SvelteSet<string>();
	const allKeys = $derived(
		(removed ?? []).flatMap((s) => s.files.map((f) => key(s.system, f.name)))
	);
	const selectedCount = $derived(allKeys.filter((k) => selected.has(k)).length);

	let deleteEntries = $state<IngestDeleteEntry[]>([]);
	let deleteOpen = $state(false);
	let deleting = $state(false);
	let restoring = $state(false);
	const disabled = $derived(!!jobs.current || deleting || restoring);

	const files = (n: number) => `${formatCount(n)} ${n === 1 ? 'file' : 'files'}`;

	function selectedEntries(): IngestDeleteEntry[] {
		return (removed ?? []).flatMap((s) =>
			s.files
				.filter((f) => selected.has(key(s.system, f.name)))
				.map((f) => ({ system: s.system, folder: 'removed' as const, name: f.name }))
		);
	}

	/** D4: Shift+click skips the confirmation (no archive exception: removed files have no Review rows). */
	function askDelete(event: MouseEvent, entries: IngestDeleteEntry[]): void {
		if (entries.length === 0 || disabled) return;
		if (event.shiftKey) void runDelete(entries);
		else {
			deleteEntries = entries;
			deleteOpen = true;
		}
	}

	async function runDelete(entries: IngestDeleteEntry[]): Promise<void> {
		deleting = true;
		try {
			await deleteIngestFiles(entries);
		} finally {
			deleting = false;
			deleteOpen = false;
		}
		void refreshCounts();
		await load();
	}

	/**
	 * D5: moves the files back to Ingestion (no confirmation); the server starts an ingest job for them. A bin/cue set
	 * must be restored together to import as a set.
	 */
	async function restore(entries: IngestDeleteEntry[]): Promise<void> {
		if (entries.length === 0 || disabled) return;
		restoring = true;
		try {
			const result = await api.post<RemovedRestoreResponse>('/api/ingest/restore', {
				files: entries.map((e) => ({ system: e.system, name: e.name }))
			});
			if (result.restored > 0) {
				if (result.jobId)
					toasts.show(`Restored ${files(result.restored)}; importing.`, 'success', {
						href: resolve('/jobs/[id]', { id: result.jobId }),
						text: 'View job'
					});
				else
					toasts.show(
						`Restored ${files(result.restored)} to Ingestion; import them there.`,
						'success'
					);
			}
			if (result.failed.length)
				toasts.show(
					`Couldn't restore: ${result.failed.map((f) => key(f.system, f.name)).join(', ')}`,
					'danger'
				);
		} catch (e) {
			if (!toasts.jobRunning(e))
				toasts.show(isApiError(e) ? e.title : "The files couldn't be restored.", 'danger');
		} finally {
			restoring = false;
		}
		void refreshCounts();
		await load();
	}

	async function load(): Promise<void> {
		error = null;
		try {
			removed = await api.get<RemovedSystem[]>('/api/ingest/removed');
			selected.clear();
		} catch (e) {
			error = e;
		}
	}

	onMount(() => {
		if (!auth.isAdmin) return;
		void load();
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (names = Object.fromEntries(list.map((s) => [s.id, s.displayName]))),
			() => {} // the badges fall back to the id
		);
		const stopWatching = jobs.watch();
		const stopEnded = jobs.onEnded(() => void load());
		return () => {
			stopWatching();
			stopEnded();
		};
	});
</script>

<svelte:head><title>Deleted ROMs · RnRomManager</title></svelte:head>

<AdminOnly>
	<PageTitle icon="delete" text="Deleted ROMs" />
	<RomToolsTabs />

	{#if error}
		<ErrorAlert {error} onretry={load} />
	{:else if !removed}
		<Loading />
	{:else if removed.length === 0}
		<div class="card"><EmptyState message="No deleted ROMs" /></div>
	{:else}
		<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
			<span class="small text-body-secondary" aria-live="polite">{selectedCount} selected</span>
			<Button
				variant="secondary"
				outline
				size="sm"
				onclick={() => allKeys.forEach((k) => selected.add(k))}>Select all</Button
			>
			<Button
				variant="secondary"
				outline
				size="sm"
				disabled={selectedCount === 0}
				onclick={() => selected.clear()}>Clear selection</Button
			>
			<Button
				variant="primary"
				outline
				size="sm"
				icon="restore"
				disabled={disabled || selectedCount === 0}
				onclick={() => restore(selectedEntries())}>Restore selected ({selectedCount})</Button
			>
			<Button
				variant="danger"
				outline
				size="sm"
				icon="delete"
				disabled={disabled || selectedCount === 0}
				{@attach fromAction(tooltip, () => 'Delete (Shift+click to skip confirmation)')}
				onclick={(e) => askDelete(e, selectedEntries())}>Delete selected ({selectedCount})</Button
			>
		</div>
		{#each removed as system (system.system)}
			<section class="mb-4" aria-labelledby="removed-{system.system}">
				<h2 class="h6 d-flex align-items-center gap-2" id="removed-{system.system}">
					<SystemBadge system={system.system} name={names[system.system] ?? system.system} />
					<span class="text-body-secondary fw-normal">{files(system.files.length)}</span>
				</h2>
				<IngestFileTable
					system={system.system}
					files={system.files}
					{selected}
					label="deleted {system.system} files"
					{disabled}
					ondelete={(e, name) => askDelete(e, [{ system: system.system, folder: 'removed', name }])}
					onrestore={(_e, name) => restore([{ system: system.system, folder: 'removed', name }])}
				/>
			</section>
		{/each}
	{/if}

	<PermanentDeleteModal
		bind:open={deleteOpen}
		entries={deleteEntries}
		busy={deleting}
		onconfirm={() => runDelete(deleteEntries)}
	/>
</AdminOnly>
