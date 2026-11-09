<script lang="ts">
	import type { CollectionListItem } from '../api/types';
	import { collectionsApi } from '../collections';
	import { toasts } from '../stores/toasts.svelte';
	import CollectionChooser from './CollectionChooser.svelte';
	import ConfirmModal from './ConfirmModal.svelte';
	import ErrorAlert from './ErrorAlert.svelte';

	/** B26: pick one collection and add the selected games to it. */
	interface Props {
		open: boolean;
		gameIds: string[];
		/** After a successful add. */
		onadded: () => void;
	}

	let { open = $bindable(), gameIds, onadded }: Props = $props();

	let chosen = $state<CollectionListItem | null>(null);
	let busy = $state(false);
	let error = $state<unknown>(null);
	const group = `collection-pick-${Math.random().toString(36).slice(2)}`;

	$effect(() => {
		if (open) {
			chosen = null;
			error = null;
		}
	});

	async function add(): Promise<void> {
		if (!chosen || busy) return;
		busy = true;
		error = null;
		try {
			const { added } = await collectionsApi.addGames(chosen.id, gameIds);
			toasts.show(`Added ${added} ${added === 1 ? 'game' : 'games'} to ${chosen.name}`, 'success');
			open = false;
			onadded();
		} catch (e) {
			error = e;
		} finally {
			busy = false;
		}
	}
</script>

<ConfirmModal
	bind:open
	title="Add {gameIds.length} {gameIds.length === 1 ? 'game' : 'games'} to a collection"
	confirmLabel="Add"
	{busy}
	confirmDisabled={!chosen}
	onconfirm={add}
>
	{#if error}<ErrorAlert {error} ondismiss={() => (error = null)} />{/if}
	<CollectionChooser active={open}>
		{#snippet item(collection)}
			<label class="list-group-item d-flex align-items-center gap-2">
				<input
					type="radio"
					class="form-check-input m-0"
					name={group}
					checked={chosen?.id === collection.id}
					onchange={() => (chosen = collection)}
				/>
				<span class="text-truncate">{collection.name}</span>
				<span class="ms-auto small text-body-secondary text-nowrap">{collection.gameCount}</span>
			</label>
		{/snippet}
	</CollectionChooser>
</ConfirmModal>
