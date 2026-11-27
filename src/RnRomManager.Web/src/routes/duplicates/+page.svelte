<script lang="ts">
	import { fromAction } from 'svelte/attachments';
	import Button from '#lib/components/forms/Button.svelte';
	import PermanentDeleteModal from '#lib/components/modals/PermanentDeleteModal.svelte';
	import PageTitle from '#lib/components/layout/PageTitle.svelte';
	import { onMount } from 'svelte';
	import { SvelteSet } from 'svelte/reactivity';
	import { api } from '#lib/api/client.js';
	import type { IngestDeleteEntry, RemovedSystem, SystemInfo } from '#lib/types/index.js';
	import AdminOnly from '#lib/components/common/AdminOnly.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import RomToolsTabs from '#lib/components/layout/RomToolsTabs.svelte';
	import IngestFileTable from '#lib/components/ingest/IngestFileTable.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { formatCount } from '#lib/format.js';
	import { deleteIngestFiles, fileKey as key } from '#lib/ingestDelete.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { refreshCounts } from '#lib/stores/navCounts.svelte.js';
	import { tooltip } from '#lib/tooltip.js';

	/** DU1–DU3: the files ingestion moved to `ingest/<system>/_duplicates/`, with permanent delete. */
	let duplicates = $state<RemovedSystem[] | null>(null);
	let names = $state<Record<string, string>>({});
	let error = $state<unknown>(null);

	// One selection across every system, keyed `system/name`; empty after each reload.
	const selected = new SvelteSet<string>();
	const all = $derived(
		(duplicates ?? []).flatMap((s) =>
			s.files.map((f) => ({ system: s.system, folder: 'duplicates' as const, name: f.name }))
		)
	);
	const selectedCount = $derived(all.filter((e) => selected.has(key(e.system, e.name))).length);
	const totalBytes = $derived((duplicates ?? []).reduce((sum, s) => sum + bytes(s), 0));

	let deleteEntries = $state<IngestDeleteEntry[]>([]);
	let deleteOpen = $state(false);
	let deleting = $state(false);
	const disabled = $derived(!!jobs.current || deleting);

	const files = (n: number) => `${formatCount(n)} ${n === 1 ? 'file' : 'files'}`;
	function bytes(system: RemovedSystem): number {
		return system.files.reduce((sum, f) => sum + f.sizeBytes, 0);
	}

	/** DU3: Shift+click skips the confirmation, unless `alwaysConfirm` (Delete all). */
	function askDelete(event: MouseEvent, entries: IngestDeleteEntry[], alwaysConfirm = false): void {
		if (entries.length === 0 || disabled) return;
		if (event.shiftKey && !alwaysConfirm) void runDelete(entries);
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

	async function load(): Promise<void> {
		error = null;
		try {
			duplicates = await api.get<RemovedSystem[]>('/api/ingest/duplicates');
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

<svelte:head><title>Duplicates · RnRomManager</title></svelte:head>

<AdminOnly>
	<PageTitle icon="report-duplicates" text="Duplicates" />
	<RomToolsTabs />

	{#if error}
		<ErrorAlert {error} onretry={load} />
	{:else if !duplicates}
		<Loading />
	{:else if duplicates.length === 0}
		<div class="card"><EmptyState message="No duplicate files." /></div>
	{:else}
		<p class="small text-body-secondary mb-2">
			{files(all.length)} using <FileSize bytes={totalBytes} />
		</p>
		<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
			<span class="small text-body-secondary" aria-live="polite">{selectedCount} selected</span>
			<Button
				variant="secondary"
				outline
				size="sm"
				onclick={() => all.forEach((e) => selected.add(key(e.system, e.name)))}>Select all</Button
			>
			<Button
				variant="secondary"
				outline
				size="sm"
				disabled={selectedCount === 0}
				onclick={() => selected.clear()}>Clear selection</Button
			>
			<Button
				variant="danger"
				outline
				size="sm"
				icon="delete"
				disabled={disabled || selectedCount === 0}
				{@attach fromAction(tooltip, () => 'Delete (Shift+click to skip confirmation)')}
				onclick={(e) =>
					askDelete(
						e,
						all.filter((f) => selected.has(key(f.system, f.name)))
					)}>Delete selected ({selectedCount})</Button
			>
			<Button
				variant="danger"
				outline
				size="sm"
				icon="delete"
				{disabled}
				onclick={(e) => askDelete(e, all, true)}
				>Delete all ({formatCount(all.length)} · <FileSize bytes={totalBytes} />)</Button
			>
		</div>
		{#each duplicates as system (system.system)}
			<section class="mb-4" aria-labelledby="duplicates-{system.system}">
				<h2 class="h6 d-flex align-items-center gap-2" id="duplicates-{system.system}">
					<SystemBadge system={system.system} name={names[system.system] ?? system.system} />
					<span class="text-body-secondary fw-normal"
						>{files(system.files.length)} · <FileSize bytes={bytes(system)} /></span
					>
				</h2>
				<IngestFileTable
					system={system.system}
					files={system.files}
					{selected}
					label="duplicate {system.system} files"
					{disabled}
					ondelete={(e, name) =>
						askDelete(e, [{ system: system.system, folder: 'duplicates', name }])}
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
