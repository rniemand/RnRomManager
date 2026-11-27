<script lang="ts">
	import { untrack } from 'svelte';
	import BaseModal from './BaseModal.svelte';
	import Checkbox from '#lib/components/forms/Checkbox.svelte';
	import FileInput from '#lib/components/forms/FileInput.svelte';
	import FormField from '#lib/components/forms/FormField.svelte';
	import Select from '#lib/components/forms/Select.svelte';
	import Textarea from '#lib/components/forms/Textarea.svelte';
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import {
		descriptionError,
		fileError,
		kindLabels,
		maxDescriptionLength,
		maxNameLength,
		nameError,
		savesApi
	} from '#lib/saves.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import type { GameDetail, SaveItem } from '#lib/types/index.js';
	import type { SaveKind } from '#lib/enums/index.js';

	type Mode = 'add' | 'edit' | 'replace' | 'delete';

	interface Props {
		/** Opening resets the form from `mode` and `save`. */
		open: boolean;
		mode: Mode | null;
		game: GameDetail;
		/** The save edited, replaced or deleted (null for add). */
		save: SaveItem | null;
		/** After the change was written (the ZIP was rewritten). */
		onsaved: () => void;
		/** S4 (add): the release a drop chose; the Release select is then fixed. */
		initialRomId?: number | null;
		/** S4 (add): the dropped file, put into the file input; the name defaults to it. */
		initialFile?: File | null;
	}

	let {
		open = $bindable(),
		mode,
		game,
		save,
		onsaved,
		initialRomId = null,
		initialFile = null
	}: Props = $props();

	let busy = $state(false);
	let error = $state<unknown>(null);
	let submitted = $state(false);
	let romId = $state<number | null>(null);
	let kind = $state<SaveKind>('state');
	let name = $state('');
	let description = $state('');
	let file = $state<File | null>(null);
	let fileInput = $state<HTMLInputElement>();

	const formId = `save-form-${Math.random().toString(36).slice(2)}`;

	$effect(() => {
		if (!open) return;
		untrack(() => {
			error = null;
			submitted = false;
			romId = initialRomId ?? game.roms[0]?.id ?? null;
			kind = 'state';
			// S4: a dropped file names the save: its name without the last extension, cut to the limit.
			name =
				save?.name ??
				(initialFile ? initialFile.name.replace(/\.[^.]*$/, '').slice(0, maxNameLength) : '');
			description = save?.description ?? '';
			file = initialFile;
			if (fileInput) {
				fileInput.value = '';
				if (initialFile) {
					const transfer = new DataTransfer();
					transfer.items.add(initialFile);
					fileInput.files = transfer.files;
				}
			}
		});
	});

	const errors = $derived({
		name: mode === 'add' || mode === 'edit' ? nameError(name) : null,
		description: mode === 'add' || mode === 'edit' ? descriptionError(description) : null,
		file: mode === 'add' || mode === 'replace' ? fileError(file) : null
	});
	const invalid = $derived(Object.values(errors).some((e) => e !== null));
	const invalidClass = (message: string | null) =>
		submitted && message ? 'is-invalid' : undefined;

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
			else if (mode === 'edit') await savesApi.update(game.id, save!.id, { name, description });
			else if (mode === 'replace') await savesApi.replace(game.id, save!.id, file!);
			else if (mode === 'delete') await savesApi.remove(game.id, save!.id);
			open = false;
			onsaved();
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

<BaseModal
	bind:open
	title={mode ? titles[mode] : ''}
	confirmLabel={mode ? confirmLabels[mode] : 'Confirm'}
	confirmClass={mode === 'delete' ? 'btn-danger' : 'btn-primary'}
	{busy}
	onconfirm={confirm}
>
	{#if error}<ErrorAlert {error} />{/if}
	{#if mode === 'delete'}
		<p class="mb-0">Delete the save <strong>{save?.name}</strong>? This can't be undone.</p>
	{:else}
		<form id={formId} novalidate onsubmit={(e) => (e.preventDefault(), confirm())}>
			{#if mode === 'add'}
				<div class="mb-3">
					<FormField label="Release" for="{formId}-release">
						<Select
							id="{formId}-release"
							bind:value={romId}
							disabled={initialRomId !== null}
							options={game.roms.map((rom) => ({ value: rom.id, label: rom.fileName }))}
						/>
					</FormField>
				</div>
				<fieldset class="mb-3" aria-describedby="{formId}-kind-help">
					<legend class="form-label fs-6">Kind</legend>
					{#each ['state', 'battery'] as const as value (value)}
						<Checkbox
							type="radio"
							inline
							id="{formId}-{value}"
							label={kindLabels[value]}
							{value}
							bind:group={kind}
						/>
					{/each}
					<!-- Release editing B21 -->
					<div id="{formId}-kind-help" class="form-text">
						Battery saves from other emulators (.sav, .srm) can be uploaded as Battery.
					</div>
				</fieldset>
			{/if}
			{#if mode === 'add' || mode === 'edit'}
				<div class="mb-3">
					<FormField label="Name" for="{formId}-name">
						<TextInput
							id="{formId}-name"
							class={invalidClass(errors.name)}
							maxlength={maxNameLength + 50}
							bind:value={name}
						/>
						{#if submitted && errors.name}<div class="invalid-feedback">{errors.name}</div>{/if}
					</FormField>
				</div>
				<div class="mb-3">
					<FormField label="Description" for="{formId}-description">
						<Textarea
							id="{formId}-description"
							class={invalidClass(errors.description)}
							rows={3}
							maxlength={maxDescriptionLength}
							bind:value={description}
						/>
						{#if submitted && errors.description}
							<div class="invalid-feedback">{errors.description}</div>
						{/if}
					</FormField>
				</div>
			{/if}
			{#if mode === 'add' || mode === 'replace'}
				<div>
					<FormField label="File" for="{formId}-file">
						<FileInput
							id="{formId}-file"
							class={invalidClass(errors.file)}
							bind:ref={fileInput}
							onchange={(e) => (file = e.currentTarget.files?.[0] ?? null)}
						/>
						{#if submitted && errors.file}<div class="invalid-feedback">{errors.file}</div>{/if}
					</FormField>
				</div>
			{/if}
		</form>
	{/if}
</BaseModal>
