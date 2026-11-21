<script lang="ts">
	import type { Snippet } from 'svelte';
	import { onDestroy, onMount } from 'svelte';
	import { Modal } from '../bootstrap';
	import { releaseModal } from '../modal';

	interface Props {
		/** Shown while true; set back to false when closed. */
		open: boolean;
		title: string;
		confirmLabel?: string;
		/** `btn-danger` for destructive actions. */
		confirmClass?: string;
		busy?: boolean;
		/** Disables only the confirm button (e.g. nothing changed yet); `busy` disables both. */
		confirmDisabled?: boolean;
		/** `lg` for a wide form (Edit metadata), `xl` for the emulator; default width otherwise. */
		size?: 'lg' | 'xl';
		/** U2: `modal-dialog-scrollable`, so the header and footer stay and only the body scrolls. */
		scrollable?: boolean;
		/** Omitted → info mode: the footer shows only "Close". */
		onconfirm?: () => void;
		/**
		 * 3DO B11: awaited before ×, Close/Cancel or Escape closes the modal, with both buttons disabled meanwhile; a
		 * rejection closes too. Omitted → those close at once.
		 */
		beforeclose?: () => Promise<void>;
		/** Review QoL B6: called each time the modal has finished opening (`shown.bs.modal`). */
		onshown?: () => void;
		children?: Snippet;
		/** U3: extra footer content, first in the footer and pushed left (`me-auto`) of Close/Cancel. */
		footer?: Snippet;
	}

	let {
		open = $bindable(),
		title,
		confirmLabel = 'Confirm',
		confirmClass = 'btn-primary',
		busy = false,
		confirmDisabled = false,
		size,
		scrollable = false,
		onconfirm,
		beforeclose,
		onshown,
		children,
		footer
	}: Props = $props();

	let closing = $state(false);

	function requestClose() {
		if (!beforeclose) {
			open = false;
			return;
		}
		if (closing) return;
		closing = true;
		beforeclose()
			.catch(() => {})
			.finally(() => {
				closing = false;
				open = false;
			});
	}

	const id = `confirm-${Math.random().toString(36).slice(2)}`;
	let element: HTMLDivElement;
	let modal = $state<InstanceType<typeof Modal> | null>(null);

	onMount(() => {
		// Bootstrap's Modal traps focus and handles Escape (spec §11 accessibility). M1: a background click never
		// closes a modal; Escape, × and the footer button do.
		modal = new Modal(element, { backdrop: 'static', keyboard: true });
		element.addEventListener('hidden.bs.modal', () => (open = false));
		// Bootstrap ignores hide() while the show transition runs, so a close during the fade-in (open already false)
		// is finished here instead of leaving an empty modal on screen.
		element.addEventListener('shown.bs.modal', () => {
			if (!open) modal?.hide();
			else onshown?.();
		});
		// 3DO B11: Escape goes through Bootstrap's hide; hold it while beforeclose runs (a hide from `open = false`
		// passes, since open is already false then).
		element.addEventListener('hide.bs.modal', (event) => {
			if (!beforeclose || !open) return;
			event.preventDefault();
			requestClose();
		});
	});

	onDestroy(() => {
		releaseModal(element); // G1: before dispose(), which drops the backdrop reference
		modal?.dispose();
	});

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
	<div
		class="modal-dialog modal-dialog-centered"
		class:modal-lg={size === 'lg'}
		class:modal-xl={size === 'xl'}
		class:modal-dialog-scrollable={scrollable}
	>
		<div class="modal-content">
			<div class="modal-header">
				<h2 class="modal-title fs-5" id="{id}-title">{title}</h2>
				<button
					type="button"
					class="btn-close"
					aria-label="Close"
					onclick={requestClose}
					disabled={closing}
				></button>
			</div>
			{#if scrollable}
				<!-- U2: a scrolling body needs keyboard access (axe scrollable-region-focusable): it takes focus itself. -->
				<!-- svelte-ignore a11y_no_noninteractive_tabindex (a focusable scroll region is the WCAG-recommended fix) -->
				<div class="modal-body" role="region" aria-label={title} tabindex="0">
					{@render children?.()}
				</div>
			{:else}
				<div class="modal-body">{@render children?.()}</div>
			{/if}
			<div class="modal-footer">
				{#if footer}
					<div class="d-flex flex-wrap gap-2 me-auto">{@render footer()}</div>
				{/if}
				<button
					type="button"
					class="btn btn-secondary"
					onclick={requestClose}
					disabled={busy || closing}>{onconfirm ? 'Cancel' : 'Close'}</button
				>
				{#if onconfirm}
					<button
						type="button"
						class="btn {confirmClass}"
						onclick={onconfirm}
						disabled={busy || confirmDisabled || closing}
					>
						{#if busy}<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>{/if}
						{confirmLabel}
					</button>
				{/if}
			</div>
		</div>
	</div>
</div>
