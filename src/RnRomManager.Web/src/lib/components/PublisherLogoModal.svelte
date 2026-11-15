<script lang="ts">
	import { isApiError } from '../api/errors';
	import type { DiscoveryGroup } from '../api/types';
	import { logoFileError, publisherArt, publisherLogoApi } from '../discovery';
	import { toasts } from '../stores/toasts.svelte';
	import ConfirmModal from './ConfirmModal.svelte';
	import Icon from './Icon.svelte';

	/**
	 * Discovery B5: replace or remove a publisher's shared logo. Any signed-in user may; the change is audited and
	 * shows for everyone. Removing it brings back the starter art, or the name alone.
	 */
	interface Props {
		open: boolean;
		publisher: DiscoveryGroup | null;
		/** After a successful upload or removal: the caller refetches, so the new `v=` URL shows at once. */
		onchanged: () => void;
	}

	let { open = $bindable(), publisher, onchanged }: Props = $props();

	const uid = Math.random().toString(36).slice(2);
	let fileInput = $state<HTMLInputElement>();
	let error = $state<string | null>(null);
	let busy = $state(false);

	const current = $derived(publisher ? publisherArt(publisher)[0] : undefined);
	const source = $derived(
		!publisher || !current
			? 'No logo: the name shows as text.'
			: current === publisher.logoUrl
				? 'Shared logo, set by a user.'
				: 'Starter art bundled with RnRomManager.'
	);

	// Each opening starts clean.
	$effect(() => {
		if (open) {
			error = null;
			if (fileInput) fileInput.value = '';
		}
	});

	function message(e: unknown): string {
		if (isApiError(e, 'ValidationFailed')) return e.errors.file?.[0] ?? e.title;
		if (isApiError(e) && e.status === 413) return 'The logo must be at most 512 KiB.';
		if (isApiError(e) && e.status === 404) return 'This publisher is no longer in the library.';
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
		const file = fileInput?.files?.[0];
		const invalid = logoFileError(file);
		if (invalid || !publisher) {
			error = invalid;
			return;
		}
		void run(
			() => publisherLogoApi.replace(publisher.key, file!),
			`${publisher.name}'s logo was saved.`
		);
	}

	function remove(): void {
		if (!publisher) return;
		void run(
			() => publisherLogoApi.remove(publisher.key),
			`${publisher.name}'s shared logo was removed.`
		);
	}
</script>

<ConfirmModal
	bind:open
	title={publisher ? `Logo for ${publisher.name}` : 'Publisher logo'}
	confirmLabel="Save logo"
	{busy}
	onconfirm={save}
>
	{#if publisher}
		<div class="d-flex align-items-center gap-3 mb-3">
			{#if current}
				<img
					class="logo-preview border rounded"
					src={current}
					alt="Current logo for {publisher.name}"
					width="160"
					height="45"
				/>
			{/if}
			<div class="small text-body-secondary">{source}</div>
		</div>

		<label class="form-label" for="logo-file-{uid}">Logo image file</label>
		<input
			id="logo-file-{uid}"
			type="file"
			class="form-control"
			class:is-invalid={error}
			accept="image/png,image/jpeg"
			aria-describedby="logo-help-{uid}"
			bind:this={fileInput}
			onchange={() => (error = null)}
		/>
		{#if error}<div class="invalid-feedback d-block" role="alert">{error}</div>{/if}
		<div class="form-text" id="logo-help-{uid}">
			PNG or JPEG, at most 512 KiB and 2048 pixels per side. It replaces the logo for everyone.
		</div>

		{#if publisher.logoUrl}
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
