<script lang="ts">
	import Checkbox from '#lib/components/forms/Checkbox.svelte';
	import { isApiError } from '#lib/api/errors.js';
	import type { CollectionListItem, CollectionRef } from '#lib/types/index.js';
	import { collectionsApi } from '#lib/collections.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import CollectionChooser from '#lib/components/forms/CollectionChooser.svelte';
	import BaseModal from './BaseModal.svelte';

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

<BaseModal bind:open title="Collections — {title}">
	<CollectionChooser active={open}>
		{#snippet item(collection)}
			<label class="list-group-item d-flex align-items-center gap-2">
				<Checkbox
					class="m-0"
					checked={memberOf.some((c) => c.id === collection.id)}
					aria-busy={pending[collection.id] ?? false}
					onchange={(e) => toggle(e.currentTarget, collection)}
				/>
				<span class="text-truncate">{collection.name}</span>
				<span class="ms-auto small text-body-secondary text-nowrap">{collection.gameCount}</span>
			</label>
		{/snippet}
	</CollectionChooser>
</BaseModal>
