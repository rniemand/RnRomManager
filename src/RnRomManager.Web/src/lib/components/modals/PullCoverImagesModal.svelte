<script lang="ts">
	import BaseModal from './BaseModal.svelte';
	import { formatCount } from '#lib/format.js';

	interface Props {
		open: boolean;
		/** Games whose cover will be replaced. */
		count: number;
		onconfirm: () => void;
	}

	let { open = $bindable(), count, onconfirm }: Props = $props();
</script>

<BaseModal bind:open title="Pull Cover Images" confirmLabel="Pull Cover Images" {onconfirm}>
	<p class="mb-0">
		{#if count === 1}
			This replaces the cover of 1 game with its IGDB cover, including covers you uploaded.
		{:else}
			This replaces the cover of {formatCount(count)} games with their IGDB cover, including covers you
			uploaded.
		{/if}
	</p>
</BaseModal>
