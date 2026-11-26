<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import type { LibraryView } from '#lib/enums/index.js';

	interface Props {
		view: LibraryView;
		onchange: (view: LibraryView) => void;
		/** `sm` on the Library page only. */
		size?: 'sm';
	}

	let { view, onchange, size }: Props = $props();
</script>

<div class="btn-group" role="group" aria-label="View">
	<Button
		variant="secondary"
		outline
		{size}
		class={view === 'grid' ? 'active' : undefined}
		aria-pressed={view === 'grid'}
		aria-label="Grid view"
		onclick={() => onchange('grid')}><Icon name="grid" /></Button
	>
	<Button
		variant="secondary"
		outline
		{size}
		class={view === 'list' ? 'active' : undefined}
		aria-pressed={view === 'list'}
		aria-label="List view"
		onclick={() => onchange('list')}><Icon name="list" /></Button
	>
</div>
