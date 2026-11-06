<script lang="ts">
	import type { Snippet } from 'svelte';
	import { onDestroy, onMount } from 'svelte';
	import { Modal } from '../bootstrap';

	interface Props {
		/** Shown while true; set back to false when closed. */
		open: boolean;
		title: string;
		confirmLabel?: string;
		/** `btn-danger` for destructive actions. */
		confirmClass?: string;
		busy?: boolean;
		/** `lg` for a wide form (Edit metadata); default width otherwise. */
		size?: 'lg';
		/** Omitted → info mode: the footer shows only "Close". */
		onconfirm?: () => void;
		children?: Snippet;
	}

	let {
		open = $bindable(),
		title,
		confirmLabel = 'Confirm',
		confirmClass = 'btn-primary',
		busy = false,
		size,
		onconfirm,
		children
	}: Props = $props();

	const id = `confirm-${Math.random().toString(36).slice(2)}`;
	let element: HTMLDivElement;
	let modal = $state<InstanceType<typeof Modal> | null>(null);

	onMount(() => {
		// Bootstrap's Modal traps focus and handles Escape (spec §11 accessibility). M1: a background click never
		// closes a modal; Escape, × and the footer button do.
		modal = new Modal(element, { backdrop: 'static', keyboard: true });
		element.addEventListener('hidden.bs.modal', () => (open = false));
	});

	onDestroy(() => modal?.dispose());

	$effect(() => {
		if (!modal) return;
		if (open) modal.show();
		else modal.hide();
	});
</script>

<div
	bind:this={element}
	class="modal fade"
	tabindex="-1"
	aria-labelledby="{id}-title"
	aria-hidden="true"
>
	<div class="modal-dialog modal-dialog-centered" class:modal-lg={size === 'lg'}>
		<div class="modal-content">
			<div class="modal-header">
				<h2 class="modal-title fs-5" id="{id}-title">{title}</h2>
				<button type="button" class="btn-close" aria-label="Close" onclick={() => (open = false)}
				></button>
			</div>
			<div class="modal-body">{@render children?.()}</div>
			<div class="modal-footer">
				<button
					type="button"
					class="btn btn-secondary"
					onclick={() => (open = false)}
					disabled={busy}>{onconfirm ? 'Cancel' : 'Close'}</button
				>
				{#if onconfirm}
					<button type="button" class="btn {confirmClass}" onclick={onconfirm} disabled={busy}>
						{#if busy}<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>{/if}
						{confirmLabel}
					</button>
				{/if}
			</div>
		</div>
	</div>
</div>
