<script lang="ts">
	import { isApiError } from '../api/errors';
	import type { CollectionListItem, CollectionRef } from '../api/types';
	import { collectionsApi } from '../collections';
	import { toasts } from '../stores/toasts.svelte';
	import CollectionChooser from './CollectionChooser.svelte';
	import ConfirmModal from './ConfirmModal.svelte';

	/** B25: tick/untick the caller's collections for one game; each change applies at once, optimistically. */
	interface Props {
		open: boolean;
		gameId: string;
		title: string;
		/** The game's collections (B16), sorted by name; updated in place. */
		memberOf: CollectionRef[];
	}

	let { open = $bindable(), gameId, title, memberOf = $bindable() }: Props = $props();

	let pending = $state<Record<string, boolean>>({});

	const sorted = (list: CollectionRef[]) =>
		[...list].sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));

	async function toggle(box: HTMLInputElement, collection: CollectionListItem): Promise<void> {
		const member = box.checked;
		// Busy rows stay enabled (disabling would drop keyboard focus out of the dialog); a click waits its turn.
		if (pending[collection.id]) {
			box.checked = !member;
			return;
		}
		const before = memberOf;
		memberOf = member
			? sorted([...before, { id: collection.id, name: collection.name }])
			: before.filter((c) => c.id !== collection.id);
		pending[collection.id] = true;
		try {
			if (member) await collectionsApi.addGames(collection.id, [gameId]);
			else await collectionsApi.removeGame(collection.id, gameId);
		} catch (e) {
			memberOf = before;
			const reason = isApiError(e) ? e.title : 'Request failed';
			toasts.show(
				`${member ? 'Adding to' : 'Removing from'} ${collection.name} failed: ${reason}`,
				'danger'
			);
		} finally {
			delete pending[collection.id];
		}
	}
</script>

<ConfirmModal bind:open title="Collections — {title}">
	<CollectionChooser active={open}>
		{#snippet item(collection)}
			<label class="list-group-item d-flex align-items-center gap-2">
				<input
					type="checkbox"
					class="form-check-input m-0"
					checked={memberOf.some((c) => c.id === collection.id)}
					aria-busy={pending[collection.id] ?? false}
					onchange={(e) => toggle(e.currentTarget, collection)}
				/>
				<span class="text-truncate">{collection.name}</span>
				<span class="ms-auto small text-body-secondary text-nowrap">{collection.gameCount}</span>
			</label>
		{/snippet}
	</CollectionChooser>
</ConfirmModal>
