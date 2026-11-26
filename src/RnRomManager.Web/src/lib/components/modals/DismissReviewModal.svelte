<script lang="ts">
	import BaseModal from './BaseModal.svelte';

	interface Props {
		open: boolean;
		/** An archive entry leaves its archive untouched; a file moves to `_failed`. */
		archiveEntry: boolean;
		busy?: boolean;
		onconfirm: () => void;
	}

	let { open = $bindable(), archiveEntry, busy = false, onconfirm }: Props = $props();
</script>

<BaseModal
	bind:open
	title="Dismiss"
	confirmLabel="Dismiss"
	confirmClass="btn-danger"
	{busy}
	{onconfirm}
>
	{#if archiveEntry}
		The archive itself is left untouched.
	{:else}
		The file moves to the system's <code>_failed</code> folder.
	{/if}
</BaseModal>
