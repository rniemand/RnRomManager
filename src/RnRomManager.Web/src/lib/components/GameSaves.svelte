<script lang="ts">
	import { untrack } from 'svelte';
	import type { GameDetail, SaveItem, SaveKind } from '../api/types';
	import { formatDateTime } from '../format';
	import {
		descriptionError,
		fileError,
		kindLabels,
		maxDescriptionLength,
		maxNameLength,
		nameError,
		savesApi
	} from '../saves';
	import { toasts } from '../stores/toasts.svelte';
	import ConfirmModal from './ConfirmModal.svelte';
	import EmptyState from './EmptyState.svelte';
	import ErrorAlert from './ErrorAlert.svelte';
	import FileSize from './FileSize.svelte';
	import Icon from './Icon.svelte';
	import Loading from './Loading.svelte';

	/** B14–B15: the game's shared saves: list, add, edit, replace, delete and download. */
	interface Props {
		game: GameDetail;
		/** B19: bumped by the page after the Play dialog saved to the library; the list reloads. */
		version?: number;
	}

	let { game, version = 0 }: Props = $props();

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

<section aria-labelledby="saves-heading">
	<div class="d-flex align-items-center gap-2 mb-3">
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
		<ErrorAlert error={loadError} onretry={load} />
	{:else if !items}
		<Loading />
	{:else if items.length === 0}
		<EmptyState message="No saves yet" />
	{:else}
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0">
				<thead class="table-dark">
					<tr>
						<th scope="col">Name</th>
						<th scope="col">Kind</th>
						<th scope="col">Release</th>
						<th scope="col" class="text-end">Size</th>
						<th scope="col">Updated</th>
						<th scope="col" class="text-end"><span class="visually-hidden">Actions</span></th>
					</tr>
				</thead>
				<tbody>
					{#each items as save (save.id)}
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
							<td class="text-end"><FileSize bytes={save.sizeBytes} /></td>
							<td class="text-nowrap">
								{formatDateTime(save.updatedAt)}
								{#if save.updatedBy}<div class="small text-body-secondary">
										{save.updatedBy}
									</div>{/if}
							</td>
							<td class="text-end text-nowrap">
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
				<fieldset class="mb-3">
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
