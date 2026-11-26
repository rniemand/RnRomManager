<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import SaveFileModal from '#lib/components/modals/SaveFileModal.svelte';
	import ConfirmModal from '#lib/components/modals/ConfirmModal.svelte';
	import { untrack } from 'svelte';
	import type { GameDetail, RomDetail, SaveItem } from '#lib/types/index.js';
	import { usesPlayDisc } from '#lib/emulator.js';
	import { formatDateTime } from '#lib/format.js';
	import { kindLabels, playableRom, savesApi } from '#lib/saves.js';
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

	function start(next: Mode, save: SaveItem | null = null): void {
		mode = next;
		target = save;
		open = true;
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

<!-- B11: one card (T1): the heading and Add save in its header, the table its direct child. -->
<section class="card" aria-labelledby="saves-heading">
	<div class="card-header d-flex align-items-center gap-2">
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
		<div class="card-body"><ErrorAlert error={loadError} onretry={load} /></div>
	{:else if !items}
		<div class="card-body"><Loading /></div>
	{:else if items.length === 0}
		<div class="card-body"><EmptyState message="No saves yet" /></div>
	{:else}
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0">
				<thead class="table-dark">
					<tr>
						<th scope="col">Name</th>
						<th scope="col">Kind</th>
						<th scope="col">Release</th>
						{#if auth.isAdmin}<th scope="col">Owner</th>{/if}
						<th scope="col" class="text-end">Size</th>
						<th scope="col">Updated</th>
						<th scope="col" class="text-end"><span class="visually-hidden">Actions</span></th>
					</tr>
				</thead>
				<tbody>
					{#each items as save (save.id)}
						{@const playable = playableRom(game, save)}
						<tr>
							<td>
								<div class="text-break">{save.name}</div>
								{#if save.description}
									<div class="small text-body-secondary text-break" style="white-space: pre-line">
										{save.description}
									</div>
								{/if}
							</td>
							<td>
								<span class="badge {save.kind === 'state' ? 'text-bg-info' : 'text-bg-secondary'}"
									>{kindLabels[save.kind]}</span
								>
							</td>
							<td class="text-truncate" style="max-width: 16rem" title={save.releaseFileName}>
								{#if save.romId === null}
									<span class="text-body-secondary">Release removed</span>
								{:else}
									{save.releaseFileName}
								{/if}
							</td>
							{#if auth.isAdmin}
								<!-- S8: an Admin lists every user's saves; a Site user sees only their own. -->
								<td class="text-nowrap">
									{#if save.uploadedBy}{save.uploadedBy}{:else}<span class="text-body-secondary"
											>Unknown</span
										>{/if}
								</td>
							{/if}
							<td class="text-end"><FileSize bytes={save.sizeBytes} /></td>
							<td class="text-nowrap">
								{formatDateTime(save.updatedAt)}
								{#if save.updatedBy}<div class="small text-body-secondary">
										{save.updatedBy}
									</div>{/if}
							</td>
							<td class="text-end text-nowrap">
								{#if playable}
									<Button
										variant="success"
										outline
										size="sm"
										icon="play"
										aria-label="Play {save.name}"
										onclick={() => play(save, playable)}
									/>
								{/if}
								<!-- eslint-disable svelte/no-navigation-without-resolve -- an API download, not a page -->
								<Button
									variant="secondary"
									outline
									size="sm"
									icon="download"
									href={savesApi.downloadUrl(game.id, save.id)}
									download
									aria-label="Download {save.name}"
								/>
								<!-- eslint-enable svelte/no-navigation-without-resolve -->
								<Button
									variant="secondary"
									outline
									size="sm"
									icon="edit"
									aria-label="Edit {save.name}"
									onclick={() => start('edit', save)}
								/>
								<Button
									variant="secondary"
									outline
									size="sm"
									icon="upload"
									aria-label="Replace {save.name}"
									onclick={() => start('replace', save)}
								/>
								<Button
									variant="danger"
									outline
									size="sm"
									icon="delete"
									aria-label="Delete {save.name}"
									onclick={() => start('delete', save)}
								/>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	{/if}
</section>

<SaveFileModal
	bind:open
	{mode}
	{game}
	save={target}
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
	onsaved={() => {
		onchanged?.();
		void load();
	}}
/>
