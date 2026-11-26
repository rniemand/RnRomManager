<script lang="ts">
	import RadioButtons from '#lib/components/forms/RadioButtons.svelte';
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import FileInput from '#lib/components/forms/FileInput.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import { isApiError } from '#lib/api/errors.js';
	import {
		discoveryText,
		logoApi,
		logoFileError,
		logoSources,
		logoUrlError
	} from '#lib/discovery.js';
	import type { LogoKind } from '#lib/enums/index.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import BaseModal from './BaseModal.svelte';

	/**
	 * B19: replace or remove a publisher's, developer's, genre's or system's shared logo (Admins only; audited, shown for everyone),
	 * from an image URL (the default each time it opens) or a file. Removing it brings back the starter art or the name.
	 */
	interface Props {
		open: boolean;
		kind: LogoKind;
		/** The group (its key) or system (its id as `key`) being changed. */
		item: { key: string; name: string; logoUrl?: string | null } | null;
		/** After a successful save or removal: the caller refetches, so the new `v=` URL shows at once. */
		onchanged: () => void;
	}

	let { open = $bindable(), kind, item, onchanged }: Props = $props();

	const noun = $derived(kind === 'systems' ? 'system' : discoveryText[kind].singular);
	const uid = Math.random().toString(36).slice(2);
	let mode = $state<'url' | 'file'>('url');
	let url = $state('');
	let fileInput = $state<HTMLInputElement>();
	let error = $state<string | null>(null);
	let busy = $state(false);

	const current = $derived(item ? logoSources(kind, item)[0] : undefined);
	const source = $derived(
		!item || !current
			? 'No logo: the name shows as text.'
			: current === item.logoUrl
				? 'Shared logo, set by a user.'
				: 'Starter art bundled with RnRomManager.'
	);

	// Each opening starts clean, on URL (the file field is re-created when File is chosen).
	$effect(() => {
		if (open) {
			mode = 'url';
			url = '';
			error = null;
		}
	});

	function message(e: unknown): string {
		if (isApiError(e, 'ValidationFailed'))
			return e.errors.url?.[0] ?? e.errors.file?.[0] ?? e.title;
		if (isApiError(e) && e.status === 413) return 'The logo must be at most 512 KiB.';
		if (isApiError(e) && e.status === 404) return `This ${noun} is no longer in the library.`;
		return isApiError(e) ? e.title : 'The logo could not be saved.';
	}

	async function run(action: () => Promise<void>, done: string): Promise<void> {
		busy = true;
		try {
			await action();
			toasts.show(done, 'success');
			open = false;
			onchanged();
		} catch (e) {
			error = message(e);
		} finally {
			busy = false;
		}
	}

	function save(): void {
		if (!item) return;
		const { key, name } = item;
		if (mode === 'url') {
			error = logoUrlError(url);
			if (!error)
				void run(() => logoApi.fromUrl(kind, key, url.trim()), `${name}'s logo was saved.`);
			return;
		}
		const file = fileInput?.files?.[0];
		error = logoFileError(file);
		if (!error) void run(() => logoApi.replace(kind, key, file!), `${name}'s logo was saved.`);
	}

	function remove(): void {
		if (!item) return;
		const { key, name } = item;
		void run(() => logoApi.remove(kind, key), `${name}'s shared logo was removed.`);
	}
</script>

<BaseModal
	bind:open
	title={item ? `Logo for ${item.name}` : 'Logo'}
	confirmLabel="Save logo"
	{busy}
	onconfirm={save}
>
	{#if item}
		<div class="d-flex align-items-center gap-3 mb-3">
			{#if current}
				<img
					class="logo-preview border rounded"
					src={current}
					alt="Current logo for {item.name}"
					width="160"
					height="45"
				/>
			{/if}
			<div class="small text-body-secondary">{source}</div>
		</div>

		<RadioButtons
			class="mb-3"
			role="radiogroup"
			aria-label="Logo source"
			name="logo-mode-{uid}"
			size="sm"
			options={[
				{ value: 'url', label: 'URL', id: `logo-mode-url-${uid}` },
				{ value: 'file', label: 'File', id: `logo-mode-file-${uid}` }
			]}
			bind:value={mode}
			onchange={() => (error = null)}
		/>

		{#if mode === 'url'}
			<label class="form-label" for="logo-url-{uid}">Logo image URL</label>
			<TextInput
				class={error ? 'is-invalid' : undefined}
				id="logo-url-{uid}"
				type="url"
				maxlength={1024}
				placeholder="https://"
				aria-describedby="logo-help-{uid}"
				bind:value={url}
				oninput={() => (error = null)}
			/>
		{:else}
			<label class="form-label" for="logo-file-{uid}">Logo image file</label>
			<FileInput
				class={error ? 'is-invalid' : undefined}
				id="logo-file-{uid}"
				accept="image/png,image/jpeg,image/webp"
				aria-describedby="logo-help-{uid}"
				bind:ref={fileInput}
				onchange={() => (error = null)}
			/>
		{/if}
		{#if error}<div class="invalid-feedback d-block" role="alert">{error}</div>{/if}
		<div class="form-text" id="logo-help-{uid}">
			PNG, JPEG or WebP (saved as PNG), at most 512 KiB and 2048 pixels per side. It replaces the
			logo for everyone.
		</div>

		{#if item.logoUrl}
			<Button
				variant="danger"
				outline
				size="sm"
				class="mt-3"
				icon="delete"
				disabled={busy}
				onclick={remove}>Remove shared logo</Button
			>
		{/if}
	{/if}
</BaseModal>

<style>
	.logo-preview {
		object-fit: contain;
	}
</style>
