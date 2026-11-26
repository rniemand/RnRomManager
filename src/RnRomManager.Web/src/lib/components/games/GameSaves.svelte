<script lang="ts">
	import { untrack } from 'svelte';
	import type { GameDetail, RomDetail, SaveItem } from '#lib/types/index.js';
	import type { SaveKind } from '#lib/enums/index.js';
	import { usesPlayDisc } from '#lib/emulator.js';
	import { formatDateTime } from '#lib/format.js';
	import {
		descriptionError,
		fileError,
		kindLabels,
		maxDescriptionLength,
		maxNameLength,
		nameError,
		playableRom,
		savesApi
	} from '#lib/saves.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import ConfirmModal from '#lib/components/modals/ConfirmModal.svelte';
	import EmulatorModal from '#lib/components/modals/EmulatorModal.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import Icon from '#lib/icons/Icon.svelte';
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
	let busy = $state(false);
	let error = $state<unknown>(null);
	let submitted = $state(false);

	let romId = $state<number | null>(null);
	let kind = $state<SaveKind>('state');
	let name = $state('');
	let description = $state('');
	let file = $state<File | null>(null);
	let fileInput = $state<HTMLInputElement | null>(null);

	const formId = `save-form-${Math.random().toString(36).slice(2)}`;

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
	const errors = $derived({
		name: mode === 'add' || mode === 'edit' ? nameError(name) : null,
		description: mode === 'add' || mode === 'edit' ? descriptionError(description) : null,
		file: mode === 'add' || mode === 'replace' ? fileError(file) : null
	});
	const invalid = $derived(Object.values(errors).some((e) => e !== null));

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

	function start(next: Mode, save: SaveItem | null = null): void {
		mode = next;
		target = save;
		error = null;
		submitted = false;
		romId = game.roms[0]?.id ?? null;
		kind = 'state';
		name = save?.name ?? '';
		description = save?.description ?? '';
		file = null;
		if (fileInput) fileInput.value = '';
		open = true;
	}

	async function confirm(): Promise<void> {
		submitted = true;
		if (mode !== 'delete' && invalid) return;
		busy = true;
		error = null;
		try {
			if (mode === 'add' && romId !== null)
				await savesApi.add(game.id, {
					romId,
					kind,
					name,
					description,
					file: file!,
					fileName: file!.name
				});
			else if (mode === 'edit') await savesApi.update(game.id, target!.id, { name, description });
			else if (mode === 'replace') await savesApi.replace(game.id, target!.id, file!);
			else if (mode === 'delete') await savesApi.remove(game.id, target!.id);
			open = false;
			onchanged?.();
			await load();
		} catch (e) {
			if (toasts.jobRunning(e)) open = false;
			else error = e;
		} finally {
			busy = false;
		}
	}

	const titles: Record<Mode, string> = {
		add: 'Add save',
		edit: 'Edit save',
		replace: 'Replace save file',
		delete: 'Delete save'
	};
	const confirmLabels: Record<Mode, string> = {
		add: 'Add save',
		edit: 'Save',
		replace: 'Replace',
		delete: 'Delete save'
	};
</script>

<!-- B11: one card (T1): the heading and Add save in its header, the table its direct child. -->
<section class="card" aria-labelledby="saves-heading">
	<div class="card-header d-flex align-items-center gap-2">
		<h2 class="h5 mb-0 me-auto" id="saves-heading">Saves</h2>
		<button
			type="button"
			class="btn btn-sm btn-primary"
			disabled={game.roms.length === 0}
			onclick={() => start('add')}
		>
			<Icon name="add" /> Add save
		</button>
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
									<button
										type="button"
										class="btn btn-sm btn-outline-success"
										aria-label="Play {save.name}"
										onclick={() => play(save, playable)}><Icon name="play" /></button
									>
								{/if}
								<!-- eslint-disable svelte/no-navigation-without-resolve -- an API download, not a page -->
								<a
									class="btn btn-sm btn-outline-secondary"
									href={savesApi.downloadUrl(game.id, save.id)}
									download
									aria-label="Download {save.name}"><Icon name="download" /></a
								>
								<!-- eslint-enable svelte/no-navigation-without-resolve -->
								<button
									type="button"
									class="btn btn-sm btn-outline-secondary"
									aria-label="Edit {save.name}"
									onclick={() => start('edit', save)}><Icon name="edit" /></button
								>
								<button
									type="button"
									class="btn btn-sm btn-outline-secondary"
									aria-label="Replace {save.name}"
									onclick={() => start('replace', save)}><Icon name="upload" /></button
								>
								<button
									type="button"
									class="btn btn-sm btn-outline-danger"
									aria-label="Delete {save.name}"
									onclick={() => start('delete', save)}><Icon name="delete" /></button
								>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	{/if}
</section>

<ConfirmModal
	bind:open
	title={mode ? titles[mode] : ''}
	confirmLabel={mode ? confirmLabels[mode] : 'Confirm'}
	confirmClass={mode === 'delete' ? 'btn-danger' : 'btn-primary'}
	{busy}
	onconfirm={confirm}
>
	{#if error}<ErrorAlert {error} />{/if}
	{#if mode === 'delete'}
		<p class="mb-0">Delete the save <strong>{target?.name}</strong>? This can't be undone.</p>
	{:else}
		<form id={formId} novalidate onsubmit={(e) => (e.preventDefault(), confirm())}>
			{#if mode === 'add'}
				<div class="mb-3">
					<label class="form-label" for="{formId}-release">Release</label>
					<select id="{formId}-release" class="form-select" bind:value={romId}>
						{#each game.roms as rom (rom.id)}<option value={rom.id}>{rom.fileName}</option>{/each}
					</select>
				</div>
				<fieldset class="mb-3" aria-describedby="{formId}-kind-help">
					<legend class="form-label fs-6">Kind</legend>
					{#each ['state', 'battery'] as const as value (value)}
						<div class="form-check form-check-inline">
							<input
								class="form-check-input"
								type="radio"
								id="{formId}-{value}"
								{value}
								bind:group={kind}
							/>
							<label class="form-check-label" for="{formId}-{value}">{kindLabels[value]}</label>
						</div>
					{/each}
					<!-- Release editing B21 -->
					<div id="{formId}-kind-help" class="form-text">
						Battery saves from other emulators (.sav, .srm) can be uploaded as Battery.
					</div>
				</fieldset>
			{/if}
			{#if mode === 'add' || mode === 'edit'}
				<div class="mb-3">
					<label class="form-label" for="{formId}-name">Name</label>
					<input
						id="{formId}-name"
						class="form-control"
						class:is-invalid={submitted && errors.name}
						maxlength={maxNameLength + 50}
						bind:value={name}
					/>
					{#if submitted && errors.name}<div class="invalid-feedback">{errors.name}</div>{/if}
				</div>
				<div class="mb-3">
					<label class="form-label" for="{formId}-description">Description</label>
					<textarea
						id="{formId}-description"
						class="form-control"
						class:is-invalid={submitted && errors.description}
						rows="3"
						maxlength={maxDescriptionLength}
						bind:value={description}></textarea>
					{#if submitted && errors.description}
						<div class="invalid-feedback">{errors.description}</div>
					{/if}
				</div>
			{/if}
			{#if mode === 'add' || mode === 'replace'}
				<div>
					<label class="form-label" for="{formId}-file">File</label>
					<input
						id="{formId}-file"
						type="file"
						class="form-control"
						class:is-invalid={submitted && errors.file}
						bind:this={fileInput}
						onchange={(e) => (file = e.currentTarget.files?.[0] ?? null)}
					/>
					{#if submitted && errors.file}<div class="invalid-feedback">{errors.file}</div>{/if}
				</div>
			{/if}
		</form>
	{/if}
</ConfirmModal>

<ConfirmModal
	bind:open={batteryOpen}
	title="Load battery save"
	confirmLabel="Play"
	onconfirm={playBattery}
>
	<p class="mb-0">Loading this battery save replaces the in-browser save for this release.</p>
</ConfirmModal>

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
