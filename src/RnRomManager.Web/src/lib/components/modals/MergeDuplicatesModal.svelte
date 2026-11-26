<script lang="ts">
	import BaseModal from './BaseModal.svelte';

	interface Props {
		open: boolean;
		/** Games merged into the target (the group's games minus the target); null while nothing is chosen. */
		count: number | null;
		/** The target game's title. */
		targetTitle: string | null;
		busy?: boolean;
		onconfirm: () => void;
	}

	let { open = $bindable(), count, targetTitle, busy = false, onconfirm }: Props = $props();
</script>

<BaseModal
	bind:open
	title="Merge duplicates"
	confirmLabel="Merge"
	confirmClass="btn-danger"
	{busy}
	{onconfirm}
>
	{#if count !== null && targetTitle !== null}
		<p class="mb-0">
			Merge {count}
			{count === 1 ? 'game' : 'games'} into
			<strong>{targetTitle}</strong>? Their releases move into it and they are deleted.
		</p>
	{/if}
</BaseModal>
