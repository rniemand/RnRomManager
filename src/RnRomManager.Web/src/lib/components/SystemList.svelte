<script lang="ts">
	import { resolve } from '$app/paths';
	import type { SystemInfo } from '../api/types';
	import { systemsSearch } from '../discovery';
	import { formatCount } from '../format';
	import { auth } from '../stores/auth.svelte';
	import GroupLogo from './GroupLogo.svelte';
	import Icon from './Icon.svelte';

	/**
	 * Systems tab B3: every supported system (0 games included), each a link selecting it, with its logo (B18) and, for
	 * Admins, a Change logo button (B19).
	 */
	interface Props {
		systems: SystemInfo[];
		selected: string | null;
		/** B19: open the logo modal for a system. */
		onedit?: (system: SystemInfo) => void;
	}

	let { systems, selected, onedit }: Props = $props();
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
					<GroupLogo
						kind="systems"
						item={{ key: system.id, name: system.displayName, logoUrl: system.logoUrl }}
					/>
					<span class="text-truncate" title={system.displayName}>{system.displayName}</span>
				</span>
				<span class="badge text-bg-secondary rounded-pill"
					>{formatCount(system.gameCount)} {system.gameCount === 1 ? 'game' : 'games'}</span
				>
			</a>
			{#if auth.isAdmin && onedit}
				<button
					type="button"
					class="btn btn-link btn-sm border-start rounded-0 px-2"
					aria-label="Change logo for {system.displayName}"
					title="Change logo"
					onclick={() => onedit(system)}><Icon name="edit" /></button
				>
			{/if}
		</li>
	{/each}
</ul>
