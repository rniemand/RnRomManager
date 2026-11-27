<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import IconButton from '#lib/components/forms/IconButton.svelte';
	import AccordionItem from '#lib/components/layout/AccordionItem.svelte';
	import DropZone from '#lib/components/common/DropZone.svelte';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import SaveFileModal from '#lib/components/modals/SaveFileModal.svelte';
	import ConfirmModal from '#lib/components/modals/ConfirmModal.svelte';
	import { untrack } from 'svelte';
	import type { GameDetail, RomDetail, SaveItem } from '#lib/types/index.js';
	import { usesPlayDisc } from '#lib/emulator.js';
	import { formatCount, formatDateTime } from '#lib/format.js';
	import { groupSaves, kindLabels, playableRom, savesApi } from '#lib/saves.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import EmulatorModal from '#lib/components/modals/EmulatorModal.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import Loading from '#lib/components/common/Loading.svelte';

	/** B14–B15: the game's saves (S2: a Site user's own, every user's for an Admin): list, add, edit, replace, delete and download. */
	interface Props {
		game: GameDetail;
		/** B19: bumped by the page after the Play dialog saved to the library; the list reloads. */
		version?: number;
		/**
		 * After each change that rewrote the ZIP: every write renumbers the release ids, so the page reloads the game
		 * (its Play and Download buttons would 404 on the old ids).
		 */
		onchanged?: () => void;
	}

	let { game, version = 0, onchanged }: Props = $props();

	let items = $state<SaveItem[] | null>(null);
	let loadError = $state<unknown>(null);

	type Mode = 'add' | 'edit' | 'replace' | 'delete';
	let mode = $state<Mode | null>(null);
	let open = $state(false);
	let target = $state<SaveItem | null>(null);

	let playOpen = $state(false);
	let playing = $state<{ rom: RomDetail; save: SaveItem } | null>(null);
	let confirmBattery = $state<{ rom: RomDetail; save: SaveItem } | null>(null);
	let batteryOpen = $state(false);

	function play(save: SaveItem, rom: RomDetail): void {
		if (save.kind === 'battery') {
			confirmBattery = { rom, save };
			batteryOpen = true;
			return;
		}
		playing = { rom, save };
		playOpen = true;
	}

	function playBattery(): void {
		batteryOpen = false;
		playing = confirmBattery;
		playOpen = true;
	}

	/** S4: the release and file a drop preset for Add save; the Add save button clears them. */
	let dropRomId = $state<number | null>(null);
	let dropFile = $state<File | null>(null);

	function start(next: Mode, save: SaveItem | null = null): void {
		mode = next;
		target = save;
		dropRomId = null;
		dropFile = null;
		open = true;
	}

	function dropped(rom: RomDetail, files: File[]): void {
		if (files.length > 1) {
			toasts.show('Drop one save file at a time.', 'warning');
			return;
		}
		start('add');
		dropRomId = rom.id;
		dropFile = files[0];
	}

	let controller: AbortController | undefined;
	async function load(): Promise<void> {
		controller?.abort();
		const current = (controller = new AbortController());
		loadError = null;
		try {
			const list = await savesApi.list(game.id, current.signal);
			if (!current.signal.aborted) items = list;
		} catch (e) {
			if (!current.signal.aborted) loadError = e;
		}
	}

	// Reload on another game and whenever `version` changes.
	$effect(() => {
		void game.id;
		void version;
		untrack(() => void load());
		return () => controller?.abort();
	});
</script>

<!-- S3: one release's saves; Release is the panel, so no Release column. -->
{#snippet savesTable(saves: SaveItem[], caption: string)}
	<div class="table-responsive">
		<table class="table table-sm table-striped table-hover align-middle mb-0">
			<caption class="visually-hidden">Saves for {caption}</caption>
			<thead class="table-dark">
				<tr>
					<th scope="col">Name</th>
					<th scope="col">Kind</th>
					{#if auth.isAdmin}<th scope="col">Owner</th>{/if}
					<th scope="col" class="text-end">Size</th>
					<th scope="col">Updated</th>
					<th scope="col" class="text-end"><span class="visually-hidden">Actions</span></th>
				</tr>
			</thead>
			<tbody>
				{#each saves as save (save.id)}
					{@const playable = playableRom(game, save)}
					<tr>
						<td>
							<div class="text-break">{save.name}</div>
							{#if save.description}
								<div class="small text-body-secondary text-break save-note">
									{save.description}
								</div>
							{/if}
						</td>
						<td>
							<span class="badge {save.kind === 'state' ? 'text-bg-info' : 'text-bg-secondary'}"
								>{kindLabels[save.kind]}</span
							>
						</td>
						{#if auth.isAdmin}
							<!-- S8: an Admin lists every user's saves; a Site user sees only their own. -->
							<td class="text-nowrap">
								{#if save.uploadedBy}{save.uploadedBy}{:else}<span class="text-body-secondary"
										>Unknown</span
									>{/if}
							</td>
						{/if}
						<td class="text-end"
							><span class="badge text-bg-light border"><FileSize bytes={save.sizeBytes} /></span
							></td
						>
						<td class="text-nowrap">
							{formatDateTime(save.updatedAt)}
							{#if save.updatedBy}<div class="small text-body-secondary">
									{save.updatedBy}
								</div>{/if}
						</td>
						<td>
							<div class="d-flex align-items-center justify-content-end gap-2">
								{#if playable}
									<IconButton
										icon="play"
										tone="success"
										label="Play {save.name}"
										tooltip="Play"
										onclick={() => play(save, playable)}
									/>
								{/if}
								<IconButton
									icon="download"
									label="Download {save.name}"
									tooltip="Download"
									href={savesApi.downloadUrl(game.id, save.id)}
									download
								/>
								<IconButton
									icon="edit"
									label="Edit {save.name}"
									tooltip="Edit"
									onclick={() => start('edit', save)}
								/>
								<IconButton
									icon="upload"
									label="Replace {save.name}"
									tooltip="Replace file"
									onclick={() => start('replace', save)}
								/>
								<IconButton
									icon="delete"
									tone="danger"
									label="Delete {save.name}"
									tooltip="Delete"
									onclick={() => start('delete', save)}
								/>
							</div>
						</td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
{/snippet}

{#snippet panel(rom: RomDetail | null, saves: SaveItem[])}
	{@const key = rom ? `saves-rom-${rom.id}` : 'saves-removed'}
	{@const title = rom?.fileName ?? 'Release removed'}
	<AccordionItem id={key} headingId="{key}-heading" headingLevel={3} open flush>
		{#snippet header()}
			<span class="text-break me-2">{title}</span>
			<span class="badge text-bg-secondary rounded-pill">{formatCount(saves.length)}</span>
		{/snippet}
		{@render savesTable(saves, title)}
	</AccordionItem>
{/snippet}

<!-- S2a: the heading row, then the panels, one per release with saves. -->
<section aria-labelledby="saves-heading">
	<div class="d-flex align-items-center gap-2 mb-3">
		<h2 class="h5 mb-0 me-auto" id="saves-heading">Saves</h2>
		<Button
			variant="primary"
			size="sm"
			icon="add"
			disabled={game.roms.length === 0}
			onclick={() => start('add')}>Add save</Button
		>
	</div>

	{#if loadError}
		<ErrorAlert error={loadError} onretry={load} />
	{:else if !items}
		<Loading />
	{:else if items.length === 0}
		<EmptyState message="No saves yet" />
	{:else}
		{#each groupSaves(game, items) as group (group.rom?.id ?? 'removed')}
			{#if group.rom}
				{@const rom = group.rom}
				<!-- S4: the whole panel, header included, takes a drop; Add save is the keyboard path. -->
				<DropZone
					hint="Drop a save file here to add it to this release"
					class="mb-3"
					ondrop={(files) => dropped(rom, files)}>{@render panel(rom, group.saves)}</DropZone
				>
			{:else}
				{@render panel(null, group.saves)}
			{/if}
		{/each}
	{/if}
</section>
<SaveFileModal
	bind:open
	{mode}
	{game}
	save={target}
	initialRomId={dropRomId}
	initialFile={dropFile}
	onsaved={() => {
		onchanged?.();
		void load();
	}}
/>

<ConfirmModal
	bind:open={batteryOpen}
	title="Load battery save"
	message="Loading this battery save replaces the in-browser save for this release."
	confirmLabel="Play"
	onconfirm={playBattery}
/>

<EmulatorModal
	bind:open={playOpen}
	gameId={game.id}
	core={game.browserCore}
	rom={playing?.rom ?? null}
	disc={playing !== null &&
		game.browserCore !== null &&
		usesPlayDisc(game.browserCore, playing.rom)}
	save={playing ? { saveId: playing.save.id, saveKind: playing.save.kind } : null}
	system={game.system}
	onsaved={() => {
		onchanged?.();
		void load();
	}}
/>

<style>
	.save-note {
		white-space: pre-line;
	}
</style>
