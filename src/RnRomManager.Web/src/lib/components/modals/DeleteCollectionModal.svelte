<script lang="ts">
	import BaseModal from './BaseModal.svelte';

	interface Props {
		open: boolean;
		/** The collection's name. */
		name: string;
		busy?: boolean;
		onconfirm: () => void;
	}

	let { open = $bindable(), name, busy = false, onconfirm }: Props = $props();
</script>

<BaseModal
	bind:open
	title="Delete {name}?"
	confirmLabel="Delete"
	confirmClass="btn-danger"
	{busy}
	{onconfirm}
>
	<p class="mb-0">
		The collection <strong>{name}</strong> is deleted. Its games stay in the Library.
	</p>
</BaseModal>
