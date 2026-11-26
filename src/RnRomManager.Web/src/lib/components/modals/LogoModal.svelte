<script lang="ts">
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
	import ConfirmModal from './ConfirmModal.svelte';
	import Icon from '#lib/icons/Icon.svelte';

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

<ConfirmModal
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

		<div class="btn-group mb-3" role="radiogroup" aria-label="Logo source">
			<input
				type="radio"
				class="btn-check"
				name="logo-mode-{uid}"
				id="logo-mode-url-{uid}"
				value="url"
				bind:group={mode}
				onchange={() => (error = null)}
			/>
			<label class="btn btn-outline-secondary btn-sm" for="logo-mode-url-{uid}">URL</label>
			<input
				type="radio"
				class="btn-check"
				name="logo-mode-{uid}"
				id="logo-mode-file-{uid}"
				value="file"
				bind:group={mode}
				onchange={() => (error = null)}
			/>
			<label class="btn btn-outline-secondary btn-sm" for="logo-mode-file-{uid}">File</label>
		</div>

		{#if mode === 'url'}
			<label class="form-label" for="logo-url-{uid}">Logo image URL</label>
			<input
				id="logo-url-{uid}"
				type="url"
				class="form-control"
				class:is-invalid={error}
				maxlength="1024"
				placeholder="https://"
				aria-describedby="logo-help-{uid}"
				bind:value={url}
				oninput={() => (error = null)}
			/>
		{:else}
			<label class="form-label" for="logo-file-{uid}">Logo image file</label>
			<input
				id="logo-file-{uid}"
				type="file"
				class="form-control"
				class:is-invalid={error}
				accept="image/png,image/jpeg,image/webp"
				aria-describedby="logo-help-{uid}"
				bind:this={fileInput}
				onchange={() => (error = null)}
			/>
		{/if}
		{#if error}<div class="invalid-feedback d-block" role="alert">{error}</div>{/if}
		<div class="form-text" id="logo-help-{uid}">
			PNG, JPEG or WebP (saved as PNG), at most 512 KiB and 2048 pixels per side. It replaces the
			logo for everyone.
		</div>

		{#if item.logoUrl}
			<button
				type="button"
				class="btn btn-sm btn-outline-danger mt-3"
				disabled={busy}
				onclick={remove}><Icon name="delete" /> Remove shared logo</button
			>
		{/if}
	{/if}
</ConfirmModal>

<style>
	.logo-preview {
		object-fit: contain;
	}
</style>
