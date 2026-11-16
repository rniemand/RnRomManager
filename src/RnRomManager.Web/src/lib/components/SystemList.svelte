<script lang="ts">
	import { resolve } from '$app/paths';
	import type { SystemInfo } from '../api/types';
	import { systemsSearch } from '../discovery';
	import Icon from './Icon.svelte';

	/** Systems tab B3: every supported system (0 games included), each a link selecting it. */
	interface Props {
		systems: SystemInfo[];
		selected: string | null;
	}

	let { systems, selected }: Props = $props();
</script>

<ul class="list-group" aria-label="Systems">
	{#each systems as system (system.id)}
		<li class="list-group-item p-0 d-flex align-items-stretch">
			<a
				class="list-group-item-action flex-grow-1 min-w-0 d-flex align-items-center justify-content-between gap-2 px-3 py-2 text-decoration-none"
				class:active={system.id === selected}
				aria-current={system.id === selected ? 'true' : undefined}
				href="{resolve('/collections')}{systemsSearch({ system: system.id, gamePage: 1 })}"
			>
				<span class="d-flex align-items-center gap-2 min-w-0">
					<Icon name="system-{system.id}" />
					<span class="text-truncate" title={system.displayName}>{system.displayName}</span>
				</span>
				<span class="badge text-bg-secondary rounded-pill"
					>{system.gameCount} {system.gameCount === 1 ? 'game' : 'games'}</span
				>
			</a>
		</li>
	{/each}
</ul>
