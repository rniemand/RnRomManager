<script lang="ts">
	import BaseModal from './BaseModal.svelte';

	interface Props {
		/** Shown while true; set back to false when closed. */
		open: boolean;
		title: string;
		/** The whole body: one plain-text paragraph. */
		message: string;
		confirmLabel?: string;
		/** `btn-danger` for destructive actions. */
		confirmClass?: string;
		busy?: boolean;
		onconfirm: () => void;
	}

	let {
		open = $bindable(),
		title,
		message,
		confirmLabel = 'Confirm',
		confirmClass = 'btn-primary',
		busy = false,
		onconfirm
	}: Props = $props();
</script>

<BaseModal bind:open {title} {confirmLabel} {confirmClass} {busy} {onconfirm}>
	<p class="mb-0">{message}</p>
</BaseModal>
