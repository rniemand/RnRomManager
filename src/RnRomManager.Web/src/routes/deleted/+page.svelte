<script lang="ts">
	import { onMount } from 'svelte';
	import { SvelteSet } from 'svelte/reactivity';
	import { api } from '#lib/api/client.js';
	import type { IngestDeleteEntry, RemovedSystem, SystemInfo } from '#lib/api/types.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import ConfirmModal from '#lib/components/ConfirmModal.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import IngestFileTable from '#lib/components/IngestFileTable.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { formatCount } from '#lib/format.js';
	import { deleteIngestFiles, fileKey as key, modalListSize } from '#lib/ingestDelete.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { refreshCounts } from '#lib/stores/navCounts.svelte.js';
	import { tooltip } from '#lib/tooltip.js';

	/** D1–D4: the files library edits moved to `ingest/<system>/_removed/`, with permanent delete. */
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
	const disabled = $derived(!!jobs.current || deleting);

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
	<h1 class="h3 mb-3"><Icon name="delete" /> Deleted ROMs</h1>

	{#if error}
		<ErrorAlert {error} onretry={load} />
	{:else if !removed}
		<Loading />
	{:else if removed.length === 0}
		<div class="card"><EmptyState message="No deleted ROMs" /></div>
	{:else}
		<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
			<span class="small text-body-secondary" aria-live="polite">{selectedCount} selected</span>
			<button
				type="button"
				class="btn btn-sm btn-outline-secondary"
				onclick={() => allKeys.forEach((k) => selected.add(k))}>Select all</button
			>
			<button
				type="button"
				class="btn btn-sm btn-outline-secondary"
				disabled={selectedCount === 0}
				onclick={() => selected.clear()}>Clear selection</button
			>
			<button
				type="button"
				class="btn btn-sm btn-outline-danger"
				disabled={disabled || selectedCount === 0}
				use:tooltip={'Delete (Shift+click to skip confirmation)'}
				onclick={(e) => askDelete(e, selectedEntries())}
				><Icon name="delete" /> Delete selected ({selectedCount})</button
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
				/>
			</section>
		{/each}
	{/if}

	<ConfirmModal
		bind:open={deleteOpen}
		title="Permanently delete {files(deleteEntries.length)}?"
		confirmLabel="Delete"
		confirmClass="btn-danger"
		busy={deleting}
		onconfirm={() => runDelete(deleteEntries)}
	>
		<p>This can't be undone.</p>
		<ul class="mb-0 text-break">
			{#each deleteEntries.slice(0, modalListSize) as entry (`${entry.system}/${entry.name}`)}
				<li><code>{entry.system}/{entry.name}</code></li>
			{/each}
			{#if deleteEntries.length > modalListSize}
				<li>and {deleteEntries.length - modalListSize} more</li>
			{/if}
		</ul>
	</ConfirmModal>
</AdminOnly>
